using System;
using System.Linq;

using Avalonia.Threading;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 上下课状态机。整条链路就靠它决定「什么时候算上课、什么时候算下课」。
///
/// 判定思路：<b>自己按课表时间算，不信任别人给的事件</b>。
/// ClassIsland 的 <c>OnClass</c> / <c>OnBreakingTime</c> 事件不一定会来（课表被停用、
/// 用户手动跳课、插件加载晚了都会错过），一旦错过就整节课不记账。所以真正的判定是
/// 每秒拿当前时间去课表里查，事件只用来把「最多 1 秒的延迟」压到毫秒级。
///
/// 三个时间点定义得很死：
/// <list type="bullet">
/// <item>上课 = 当前时间进入某个 <c>TimeType=0</c> 的块；</item>
/// <item>下课 = 离开那个块，结算时间取<b>课表上的下课时刻</b>，不是定时器醒来的时刻；</item>
/// <item>课间 = 落在 <c>TimeType=1</c> 的块里，或者落在两块之间的缝里。</item>
/// </list>
/// </summary>
public static class LessonClock
{
    /// <summary>一节课最长算这么久。超过就截断——半天的课多半是休眠没结算出来的假数据。</summary>
    private const double MaxLessonHours = 3.0;

    /// <summary>时间往前跳超过这个秒数，当成休眠 / 卡死，先补结算再重来。</summary>
    private const double JumpForwardSeconds = 90;

    /// <summary>时间往回退超过这个秒数，当成校时，不结算，只重新对齐基线。</summary>
    private const double JumpBackSeconds = 3;

    /// <summary>课表读不到之后，撑这么久还没恢复就先把课上结了，免得挂一整天。</summary>
    private const double UnavailableGraceSeconds = 90;

    private static readonly object Gate = new();

    private static DispatcherTimer? _timer;
    private static bool _running;

    private static DateTime _last;
    private static ScheduleBlock? _currentBlock;
    private static DateTime _unavailableSince = DateTime.MinValue;
    private static LessonPhase _pendingPhase;
    private static int _pendingCount;

    /// <summary>手动模拟的课正在上：状态机不要拿课表来把它顶掉。</summary>
    private static bool _manualHold;

    // ------------------------------------------------------------------
    // 对外状态
    // ------------------------------------------------------------------

    public static LessonPhase Phase { get; private set; } = LessonPhase.Unknown;

    public static ScheduleBlock? CurrentBlock => _currentBlock;

    /// <summary>正在上的这节课。课间为 null。</summary>
    public static LessonContext? Current { get; private set; }

    /// <summary>最近一次异常处理的说明，界面会显示出来。</summary>
    public static string Warning { get; private set; } = "";

    public static bool IsRunning => _running;

    /// <summary>正在上课时触发，参数是这节的身份证。</summary>
    public static event Action<LessonContext>? LessonStarted;

    /// <summary>下课时触发。<see cref="LessonEndReason"/> 决定要不要弹总结窗。</summary>
    public static event Action<LessonContext, LessonEndReason>? LessonEnded;

    /// <summary>每一拍都会触发，给界面刷状态用。</summary>
    public static event Action? Ticked;

    // ------------------------------------------------------------------
    // 启停
    // ------------------------------------------------------------------

    public static void Start()
    {
        lock (Gate)
        {
            if (_running) return;
            _running = true;

            _last = TimetableSync.Now();
            _unavailableSince = DateTime.MinValue;
            _pendingCount = 0;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Pulse();
            _timer.Start();
        }

        // 启动先补一拍：插件可能是在课中被启用的
        Pulse(forced: true);
    }

    /// <summary>把状态机清回初始值。插件卸载后重载、或者自检脚本换场景时用。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _timer?.Stop();
            _timer = null;
            _running = false;

            _last = DateTime.MinValue;
            _currentBlock = null;
            _unavailableSince = DateTime.MinValue;
            _pendingPhase = LessonPhase.Unknown;
            _pendingCount = 0;
            _manualHold = false;

            Current = null;
            Phase = LessonPhase.Unknown;
            Warning = "";
        }
    }

    /// <summary>没人启动过就启动。任何地方都可以安全地调，重复调用无副作用。</summary>
    public static void EnsureStarted()
    {
        try
        {
            if (!_running) Start();
        }
        catch
        {
            // 还没到能建定时器的时候（比如 Avalonia 都没起来），下一次再试
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _running = false;
            _timer?.Stop();
            _timer = null;

            // 插件卸载 / ClassIsland 退出：手上这节先结掉，别丢数据
            if (Current != null) Finish(Current, TimetableSync.Now(), LessonEndReason.Wake);
        }
    }

    /// <summary>
    /// 立刻判定一次，返回是否发生了相位切换。
    /// 课表事件到达时调它，把「等下一次定时器」的延迟抹掉。
    /// </summary>
    public static bool PulseNow() => Pulse(forced: true);

    // ------------------------------------------------------------------
    // 一拍
    // ------------------------------------------------------------------

    private static bool Pulse(bool forced = false)
    {
        if (!_running) return false;

        bool changed;

        lock (Gate)
        {
            changed = AdvanceLocked(TimetableSync.Now(), forced);
        }

        SafeFire(Ticked);
        return changed;
    }

    /// <summary>
    /// 拿一个<b>指定的时刻</b>推进一拍，返回是否发生了相位切换。
    /// 定时器走的是 <see cref="TimetableSync.Now()"/>，而这个入口不读系统时间，
    /// 所以可以喂假时间把一整天快进一遍 —— 自检脚本就是靠它验证判定逻辑的。
    /// </summary>
    public static bool AdvanceTo(DateTime now)
    {
        bool changed;

        lock (Gate)
        {
            changed = AdvanceLocked(now, forced: true);
        }

        SafeFire(Ticked);
        return changed;
    }

    /// <summary>一拍的真正内容。调用方负责加锁。</summary>
    private static bool AdvanceLocked(DateTime now, bool forced)
    {
        TimetableSync.Refresh(forced);

        // 第一拍没有基线，直接对齐，别把"从 0001 年到现在"当成时钟跳变
        if (_last == DateTime.MinValue)
        {
            _last = now;
            ResetTo(now);
            return true;
        }

        var delta = (now - _last).TotalSeconds;

        // 时钟被拨动 / 机器睡了一觉：这一拍不按常规走
        if (delta > JumpForwardSeconds || delta < -JumpBackSeconds)
        {
            HandleJump(now, delta);
            _last = now;
            return true;
        }

        if (_manualHold && Current != null)
        {
            // 手动开的课，课表说了不算；等用户点【模拟下课】才结束
            Phase = LessonPhase.InClass;
            return false;
        }

        var changed = Evaluate(now);
        _last = now;

        return changed;
    }

    // ------------------------------------------------------------------
    // 异常：时钟跳变
    // ------------------------------------------------------------------

    private static void HandleJump(DateTime now, double delta)
    {
        if (delta > JumpForwardSeconds)
        {
            // 往前跳：休眠唤醒、系统卡死、笔记本合盖。这一段没有画面，不能算进课时。
            Warning = $"时钟中断了 {Describe(delta)}（休眠或长时间卡顿），这段不计入课时，已按中断前的时刻结算。";

            // 用中断前最后一拍的时间结算：那之后我们什么都没看见
            if (Current != null) Finish(Current, _last, LessonEndReason.Wake);
        }
        else
        {
            // 往回退：多半是刚做了网络校时。倒着算会把课时算成负数，
            // 所以这里什么都不做——连 ResetTo 都不能调，那会把正在上的课判成"还没上课"直接结掉。
            Warning = $"时钟倒退了 {Describe(-delta)}（可能刚校时），这节课继续，不做结算。";
            return;
        }

        // 往前跳之后相位一定得重算，但不能按常规那套「边界穿越立即生效」——
        // 否则一次十几小时的跳跃会连着触发一串假的上课下课。这里直接重设，不发事件。
        ResetTo(now);
    }

    /// <summary>把相位硬切到当前时间对应的值，不产生任何事件。跳变后专用。</summary>
    private static void ResetTo(DateTime now)
    {
        if (_manualHold && Current != null) return;

        var (phase, block) = Compute(now);

        Phase = phase;
        _pendingCount = 0;
        _unavailableSince = DateTime.MinValue;

        if (phase == LessonPhase.InClass && block != null && Current == null)
        {
            // 跳变落在一节课中间：从这一刻开始记，不追述跳变前
            Begin(now, block);
            return;
        }

        if (phase != LessonPhase.InClass && Current != null)
        {
            Finish(Current, now, LessonEndReason.Wake);
            return;
        }

        _currentBlock = block;
    }

    // ------------------------------------------------------------------
    // 常规判定
    // ------------------------------------------------------------------

    /// <summary>返回是否发生了相位切换。</summary>
    private static bool Evaluate(DateTime now)
    {
        // 时钟被拨回去了：现在比"上课时刻"还早。
        // 这时候按课表算出来的相位是"还没上课"，但课明明上着 —— 先按住，等时间走回来。
        if (Current != null && now < Current.Start)
        {
            Phase = LessonPhase.InClass;
            _pendingCount = 0;
            return false;
        }

        var (phase, block) = Compute(now);

        // 课表不可用：给一段宽限期，超时就把课上结了，免得挂一整天
        if (phase == LessonPhase.Unknown)
        {
            if (_unavailableSince == DateTime.MinValue) _unavailableSince = now;

            if (Current != null &&
                (now - _unavailableSince).TotalSeconds > UnavailableGraceSeconds)
            {
                Warning = "课表已经有 90 秒读不到了，先按当前时刻把课上结了。恢复后会自动继续。";
                Finish(Current, now, LessonEndReason.Timetable);
                Phase = LessonPhase.Unknown;
                _currentBlock = null;
                return true;
            }

            Phase = LessonPhase.Unknown;
            _currentBlock = null;
            return false;
        }

        _unavailableSince = DateTime.MinValue;

        if (phase == Phase)
        {
            _pendingCount = 0;
            _currentBlock = block;
            return false;
        }

        // 相位变了。要不要立刻生效？
        // 只有当「上一次判定到现在之间真的跨过了一个课表边界」才算数得准，立即生效；
        // 其余情况（课表刚加载、刚恢复、数据抖动）要求连续两拍一致，避免闪来闪去。
        if (!CrossedBoundary(_last, now))
        {
            if (_pendingPhase != phase)
            {
                _pendingPhase = phase;
                _pendingCount = 1;
                return false;
            }

            _pendingCount++;
            if (_pendingCount < 2) return false;
        }

        _pendingCount = 0;
        Apply(now, phase, block);
        return true;
    }

    /// <summary>(上次判定, 本次判定] 区间里有没有落进一个课表边界。</summary>
    private static bool CrossedBoundary(DateTime from, DateTime to)
    {
        var day = TimetableSync.Today;
        if (day == null) return false;
        if (from == default || from > to) return false;

        var a = from.TimeOfDay;
        var b = to.TimeOfDay;   // 跨零点不会走到这里：Refresh 已经把 Today 换成新的一天

        foreach (var block in day.Blocks)
        {
            if (a < block.Start && block.Start <= b) return true;
            if (a < block.End && block.End <= b) return true;
        }

        return false;
    }

    private static void Apply(DateTime now, LessonPhase phase, ScheduleBlock? block)
    {
        Phase = phase;

        switch (phase)
        {
            case LessonPhase.InClass when block != null:

                // 换节：先结掉旧的，再开新的
                if (Current != null && Current.Index != block.Index)
                {
                    Finish(Current, SettleTime(now), LessonEndReason.Timetable);
                }

                if (Current == null) Begin(now, block);

                _currentBlock = block;
                break;

            case LessonPhase.Breaking:
            case LessonPhase.AfterSchool:
            case LessonPhase.BeforeSchool:

                if (Current != null)
                {
                    var reason = phase == LessonPhase.AfterSchool
                        ? LessonEndReason.SchoolOver
                        : LessonEndReason.Timetable;

                    Finish(Current, SettleTime(now), reason);
                }

                _currentBlock = block;
                break;

            default:
                _currentBlock = block;
                break;
        }
    }

    /// <summary>
    /// 结算时刻。
    /// 如果这一拍才越过课表上的下课点（比如定时器卡了两秒），就用<b>课表上的下课时刻</b>，
    /// 而不是现在。这样曲线和时长都不会因为 UI 卡顿而被拉长。
    /// </summary>
    private static DateTime SettleTime(DateTime now)
    {
        if (_currentBlock != null && Current != null)
        {
            var planned = _currentBlock.EndOf(now);

            if (planned <= now && planned > Current.Start) return planned;
        }

        return now;
    }

    private static void Begin(DateTime now, ScheduleBlock block)
    {
        var context = new LessonContext
        {
            Index = block.Index,
            Subject = block.Label,
            Start = now,
            PlannedEnd = block.EndOf(now),
            FromTimetable = true
        };

        Current = context;
        _currentBlock = block;

        SafeFire(() => LessonStarted?.Invoke(context));
    }

    private static void Finish(LessonContext context, DateTime at, LessonEndReason reason)
    {
        if (!ReferenceEquals(Current, context))
        {
            Current = null;
            _currentBlock = null;
            return;
        }

        // 结束时间不能早于开始时间，也不能超过上限
        if (at < context.Start) at = context.Start;

        if ((at - context.Start).TotalHours > MaxLessonHours)
        {
            at = context.Start.AddHours(MaxLessonHours);
            reason = LessonEndReason.Truncated;
            Warning = $"这节课超过 {MaxLessonHours:0} 小时，按上限截断了——多半是中间休眠没结算。";
        }

        context.End = at;

        Current = null;
        _currentBlock = null;
        _manualHold = false;

        SafeFire(() => LessonEnded?.Invoke(context, reason));
    }

    // ------------------------------------------------------------------
    // 判定核心
    // ------------------------------------------------------------------

    private static (LessonPhase Phase, ScheduleBlock? Block) Compute(DateTime now)
    {
        var day = TimetableSync.Today;

        // 课表没读到、没内容、或者被停用，一律不自动上下课
        if (day == null || day.Blocks.Count == 0 || !TimetableSync.IsUsable)
        {
            return (LessonPhase.Unknown, null);
        }

        var timeOfDay = now.TimeOfDay;

        var block = day.BlockAt(timeOfDay);

        if (block != null)
        {
            return block.IsClass
                ? (LessonPhase.InClass, block)
                : (LessonPhase.Breaking, block);
        }

        if (timeOfDay < day.FirstStart) return (LessonPhase.BeforeSchool, null);
        if (timeOfDay >= day.LastEnd) return (LessonPhase.AfterSchool, null);

        // 两块之间的缝：不是我们排的，也不会持续多久，当课间处理
        return (LessonPhase.Breaking, null);
    }

    // ------------------------------------------------------------------
    // 手动模拟：给试弹用
    // ------------------------------------------------------------------

    /// <summary>
    /// 手动开一节课，立刻触发 <see cref="LessonStarted"/>。
    /// 用来验证「上课 → 记账 → 下课 → 弹窗」整条链路，不用真等一节课。
    /// </summary>
    public static LessonContext SimulateBegin()
    {
        lock (Gate)
        {
            var now = TimetableSync.Now();

            // 手上有课就先结掉，避免两节课叠在一起
            if (Current != null) Finish(Current, now, LessonEndReason.Manual);

            var block = TimetableSync.Today?.BlockAt(now.TimeOfDay);

            var context = new LessonContext
            {
                Index = block?.Index ?? 0,
                Subject = string.IsNullOrWhiteSpace(block?.Label) ? "手动模拟" : block!.Label,
                Start = now,
                PlannedEnd = now.AddMinutes(40),
                FromTimetable = false
            };

            Current = context;
            _currentBlock = null;
            _manualHold = true;
            Phase = LessonPhase.InClass;

            SafeFire(() => LessonStarted?.Invoke(context));

            return context;
        }
    }

    /// <summary>手动下课，触发 <see cref="LessonEnded"/>（reason 为 <see cref="LessonEndReason.Manual"/>）。</summary>
    public static LessonContext? SimulateEnd()
    {
        lock (Gate)
        {
            if (Current == null) return null;

            var context = Current;
            Finish(context, TimetableSync.Now(), LessonEndReason.Manual);
            Phase = LessonPhase.Breaking;

            return context;
        }
    }

    // ------------------------------------------------------------------
    // 给界面
    // ------------------------------------------------------------------

    public static PhaseSnapshot Snapshot()
    {
        var now = TimetableSync.Now();
        var day = TimetableSync.Today;

        if (!TimetableSync.IsUsable || day == null)
        {
            return new PhaseSnapshot
            {
                Phase = LessonPhase.Unknown,
                Title = TimetableSync.State switch
                {
                    SyncState.NoService => "课表服务不可用",
                    SyncState.NoPlan => "今天没有课",
                    SyncState.Disabled => "课表未启用",
                    SyncState.Empty => "课表是空的",
                    SyncState.Failed => "课表读取失败",
                    _ => "还没同步课表"
                },
                Detail = TimetableSync.Message,
                Next = "自动上下课已暂停，可以手动启停监测"
            };
        }

        var timeOfDay = now.TimeOfDay;
        var block = day.BlockAt(timeOfDay);
        var next = day.NextClassAfter(timeOfDay);

        var nextText = next != null
            ? $"下一节 {next.Start:hh\\:mm} · {Label(next)}"
            : "";

        switch (Phase)
        {
            case LessonPhase.InClass when block != null:
                return new PhaseSnapshot
                {
                    Phase = Phase,
                    Title = "正在上课 · " + Label(block),
                    Detail = $"还剩 {Clock(block.EndOf(now) - now)}（{block.End:hh\\:mm} 下课）· 已上 {Clock(now - block.StartOf(now))}",
                    Next = nextText
                };

            case LessonPhase.Breaking:
                return new PhaseSnapshot
                {
                    Phase = Phase,
                    Title = block != null ? "课间 · " + Label(block) : "课间",
                    Detail = block != null
                        ? $"还剩 {Clock(block.EndOf(now) - now)}（{block.End:hh\\:mm} 上课）"
                        : "两块之间的空档",
                    Next = nextText
                };

            case LessonPhase.BeforeSchool:
                return new PhaseSnapshot
                {
                    Phase = Phase,
                    Title = "还没到第一节课",
                    Detail = $"第一节 {day.FirstStart:hh\\:mm} 开始，还有 {Clock(day.FirstStart - timeOfDay)}",
                    Next = nextText
                };

            case LessonPhase.AfterSchool:
                return new PhaseSnapshot
                {
                    Phase = Phase,
                    Title = "今天的课上完了",
                    Detail = $"最后一节 {day.LastEnd:hh\\:mm} 结束",
                    Next = ""
                };

            default:
                return new PhaseSnapshot
                {
                    Phase = Phase,
                    Title = "状态未知",
                    Detail = TimetableSync.Message,
                    Next = nextText
                };
        }
    }

    private static string Label(ScheduleBlock block)
        => block.IsClass
            ? (string.IsNullOrWhiteSpace(block.Label) ? $"第 {block.Index} 节" : $"第 {block.Index} 节 · {block.Label}")
            : block.Label;

    private static string Clock(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    private static string Describe(double seconds)
        => seconds >= 3600 ? $"{seconds / 3600:0.#} 小时" : $"{seconds / 60:0.#} 分钟";

    private static void SafeFire(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch
        {
            // 一个订阅者抛异常不能把状态机带崩
        }
    }
}
