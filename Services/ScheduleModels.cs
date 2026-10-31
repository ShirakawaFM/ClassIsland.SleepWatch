using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassIsland.SleepWatch.Services;

// ============================================================================
// 课表相关的数据结构。
//
// 这一层刻意不引用 ClassIsland 的类：TimetableSync 负责把 ClassIsland 的
// ClassPlan / TimeLayoutItem 翻译成下面这几个类型，其余代码只认这几个类型。
// 好处是状态机、界面、单元测试都不需要 ClassIsland 在跑。
// ============================================================================

/// <summary>时间表里一个块的种类。分割线（<c>TimeType=2</c>）和行动点（<c>3</c>）是零长度的，直接丢掉。</summary>
public enum BlockKind
{
    /// <summary>上课。ClassIsland 的 <c>TimeType=0</c>。</summary>
    Class,

    /// <summary>课间。ClassIsland 的 <c>TimeType=1</c>。</summary>
    Break
}

/// <summary>课表里的一个时间段。</summary>
public sealed class ScheduleBlock
{
    /// <summary>第几节课（从 1 起）。课间固定为 0。</summary>
    public int Index { get; init; }

    public BlockKind Kind { get; init; }

    /// <summary>当天的起始时刻（只到时分秒，日期由所属的 <see cref="ScheduleDay"/> 决定）。</summary>
    public TimeSpan Start { get; init; }

    public TimeSpan End { get; init; }

    /// <summary>上课是科目名，课间是「大课间」这类名字。</summary>
    public string Label { get; init; } = "";

    public bool IsClass => Kind == BlockKind.Class;

    public TimeSpan Length => End > Start ? End - Start : TimeSpan.Zero;

    /// <summary>把「当天的 10:20」拼成完整的日期时间。</summary>
    public DateTime At(DateTime date, TimeSpan time) => date.Date + time;

    public DateTime StartOf(DateTime date) => date.Date + Start;

    public DateTime EndOf(DateTime date) => date.Date + End;

    /// <summary>这块在给定时刻是否正在进行。<c>End</c> 那一瞬间算结束，不占下一块的起点。</summary>
    public bool Covers(TimeSpan timeOfDay) => timeOfDay >= Start && timeOfDay < End;

    public override string ToString()
        => $"{Start:hh\\:mm}-{End:hh\\:mm} {(IsClass ? $"第{Index}节 {Label}" : Label)}";
}

/// <summary>某一天的课表。同步一次得到一份，之后只读。</summary>
public sealed class ScheduleDay
{
    public DateTime Date { get; init; }

    /// <summary>课表名，ClassIsland 里叫「课表 / 时间表方案」。</summary>
    public string PlanName { get; init; } = "";

    /// <summary>课表在 ClassIsland 里是否处于启用状态。禁用时时间照读，但不自动上下课。</summary>
    public bool PlanEnabled { get; init; }

    /// <summary>按开始时间排好序的上课与课间块。</summary>
    public IReadOnlyList<ScheduleBlock> Blocks { get; init; } = Array.Empty<ScheduleBlock>();

    public DateTime SyncedAt { get; init; }

    public int ClassCount => Blocks.Count(b => b.IsClass);

    public TimeSpan FirstStart => Blocks.Count > 0 ? Blocks.Min(b => b.Start) : TimeSpan.Zero;

    public TimeSpan LastEnd => Blocks.Count > 0 ? Blocks.Max(b => b.End) : TimeSpan.Zero;

    /// <summary>某个时刻落在哪一块。落在两块之间的缝里返回 null。</summary>
    public ScheduleBlock? BlockAt(TimeSpan timeOfDay)
    {
        foreach (var block in Blocks)
        {
            if (block.Covers(timeOfDay)) return block;
        }

        return null;
    }

    /// <summary>这一刻之后的第一节上课（不含正在上的那一节）。</summary>
    public ScheduleBlock? NextClassAfter(TimeSpan timeOfDay)
        => Blocks.FirstOrDefault(b => b.IsClass && b.Start > timeOfDay);

    public override string ToString()
        => $"{Date:MM-dd} {PlanName} · {ClassCount} 节";
}

/// <summary>检测出来的相位。名字故意跟 ClassIsland 的 <c>TimeState</c> 区分开：这是我们自己按时间算的。</summary>
public enum LessonPhase
{
    /// <summary>课表读不到 / 没配置 / 出错。此时不自动上下课。</summary>
    Unknown,

    /// <summary>今天第一节还没开始。</summary>
    BeforeSchool,

    /// <summary>正在上课。</summary>
    InClass,

    /// <summary>课间，或者两块之间的空隙。</summary>
    Breaking,

    /// <summary>最后一节已经上完。</summary>
    AfterSchool
}

/// <summary>一节课的身份证。上课时创建，下课时交给记账和弹窗。</summary>
public sealed class LessonContext
{
    public int Index { get; init; }

    public string Subject { get; init; } = "";

    public DateTime Start { get; init; }

    /// <summary>课表上的下课时刻。提前下课的话这个值会大于真实结束时间。</summary>
    public DateTime PlannedEnd { get; init; }

    /// <summary>实际结算时刻。由状态机在结课时填，账本拿它当这一节的结束时间。</summary>
    public DateTime End { get; set; }

    /// <summary>false 表示这节课是手动模拟出来的。</summary>
    public bool FromTimetable { get; init; } = true;

    public string Title => Index > 0 ? $"第 {Index} 节 · {Subject}" : Subject;

    public override string ToString() => $"{Title} @ {Start:HH:mm}";
}

/// <summary>这节课是怎么结束的。决定了要不要弹窗。</summary>
public enum LessonEndReason
{
    /// <summary>课表说到点了。</summary>
    Timetable,

    /// <summary>最后一节上完，放学。</summary>
    SchoolOver,

    /// <summary>时钟中断（休眠 / 长时间卡顿）后补结算。这段没有画面，所以不弹窗。</summary>
    Wake,

    /// <summary>课时超过上限被截断。</summary>
    Truncated,

    /// <summary>手动模拟下课。</summary>
    Manual
}

/// <summary>给界面看的一行状态。</summary>
public sealed class PhaseSnapshot
{
    public LessonPhase Phase { get; init; }

    /// <summary>第一行：现在到底在干什么。</summary>
    public string Title { get; init; } = "";

    /// <summary>第二行：还剩多久 / 已经多久。</summary>
    public string Detail { get; init; } = "";

    /// <summary>第三行：下一节是什么。</summary>
    public string Next { get; init; } = "";
}

/// <summary>课表同步的健康状况。</summary>
public enum SyncState
{
    /// <summary>还没读过。</summary>
    NotReady,

    /// <summary>读到了，而且当天有课。</summary>
    Ok,

    /// <summary>拿不到 <c>ILessonsService</c>：插件可能加载得比课表服务早，或者跑在精简环境里。</summary>
    NoService,

    /// <summary>当天没有匹配的课表（周末 / 假期 / 没配置）。</summary>
    NoPlan,

    /// <summary>有课表但被停用了。</summary>
    Disabled,

    /// <summary>课表存在，但里面一个时间段都没有。</summary>
    Empty,

    /// <summary>读的过程中抛异常了。</summary>
    Failed
}
