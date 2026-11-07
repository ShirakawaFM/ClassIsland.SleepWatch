using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassIsland.SleepWatch.Services;

// ============================================================================
// 静默检测的账本。
//
// 检测线程每帧都会把"谁确认睡着了 / 谁醒了"喂进来，这里把它记成一段一段的睡眠事件。
// 全程不弹任何东西、不改主界面，就是安静地记账 —— 下课铃一响，账本交给课后总结。
//
// 1.5 起每节课有独立的课程 ID，事件和曲线点都绑在这个 ID 上，
// 课后总结按 ID 精确查询，不会再把两节课的数据搅在一起。
//
// 所有写入都走 _gate：采样在检测线程，结算在 UI 线程，两边都会碰这几个集合。
// ============================================================================

/// <summary>账本本体。单例，检测线程和 UI 线程都会碰，内部全部加锁。</summary>
public sealed class SleepLedger
{
    public static SleepLedger Instance { get; } = new();

    private readonly object _gate = new();
    private readonly List<DayRecord> _days = new();
    private readonly Dictionary<int, SleepEpisode> _open = new();

    private LessonRecord? _current;

    /// <summary>下课结算完之后先别再自动开新的一节，否则课间那点画面会被记成一节"课"。</summary>
    private bool _autoPaused;
    private DateTime _lastSample = DateTime.MinValue;
    private bool _dirty;
    private bool _loaded;

    /// <summary>历史只留这么多天，免得 json 无限长大。</summary>
    private const int KeepDays = 120;

    /// <summary>曲线采样间隔。一节课 45 分钟大约 135 个点，够画了。</summary>
    private const double SampleSeconds = 20;

    /// <summary>采样断档超过这么多分钟，就认为中间没看着画面，开着的睡眠段按断档前的时刻收口。</summary>
    private const double GapMinutes = 5;

    /// <summary>一节课结束时会触发，参数是刚结算完的那节课。</summary>
    public event Action<LessonRecord>? LessonFinished;

    /// <summary>刚结束的那节课。课后总结优先用它。</summary>
    public LessonRecord? LastFinished { get; private set; }

    /// <summary>当前正在记的那节课（课间为 null）。</summary>
    public LessonRecord? Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    // ---------------- 生命周期 ----------------

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Load();
    }

    /// <summary>
    /// 上课：开一个新的记录段。
    /// <paramref name="index"/> 由课表给（第几节），传 0 就按当天的顺序自己编号。
    /// </summary>
    public void BeginLesson(DateTime now, int classSize, int index = 0, string subject = "", bool manual = false)
    {
        lock (_gate)
        {
            EnsureLoaded();

            // 上一节没收尾（比如中途点了重启），先补一刀
            if (_current != null) CloseCurrent(now);

            var day = DayOf(now);
            if (index <= 0) index = day.Lessons.Count + 1;

            _autoPaused = false;

            _current = new LessonRecord
            {
                Id = NewLessonId(day, now, index),
                Start = now,
                End = now,
                Index = index,
                Subject = subject ?? "",
                ClassSize = classSize,
                Manual = manual
            };

            day.Lessons.Add(_current);
            _open.Clear();
            _lastSample = DateTime.MinValue;

            _dirty = true;
        }
    }

    /// <summary>课程 ID：日期 + 节次，撞了就加序号。跨天不会重复，同一节重启也不会覆盖旧记录。</summary>
    private static string NewLessonId(DayRecord day, DateTime now, int index)
    {
        var prefix = index > 0
            ? $"{now:yyyyMMdd}-{index:D2}"
            : $"{now:yyyyMMdd}-M{now:HHmm}";

        if (day.Lessons.All(l => l.Id != prefix)) return prefix;

        for (var i = 2; i < 100; i++)
        {
            var candidate = $"{prefix}-{i}";
            if (day.Lessons.All(l => l.Id != candidate)) return candidate;
        }

        return prefix + "-x";
    }

    /// <summary>下课：收尾、落盘，返回这节课的记录。</summary>
    public LessonRecord? EndLesson(DateTime now)
    {
        LessonRecord? finished;

        lock (_gate)
        {
            EnsureLoaded();
            finished = _current;
            if (finished == null) return null;

            CloseCurrent(now);
            LastFinished = finished;
            _autoPaused = true;
            SaveLocked();
        }

        try
        {
            LessonFinished?.Invoke(finished);
        }
        catch (Exception ex)
        {
            AppLog.Error("结算回调失败", ex);
        }

        return finished;
    }

    private void CloseCurrent(DateTime now)
    {
        if (_current == null) return;

        foreach (var ep in _open.Values.ToList())
        {
            if (ep.End == null) ep.End = now;
        }

        _open.Clear();
        _current.End = now > _current.Start ? now : _current.Start;
        _current = null;
    }

    /// <summary>没有课表也要能用：采样进来时若没有开着的课，就自己开一段。</summary>
    private void EnsureLesson(DateTime now, int classSize)
    {
        if (_current != null) return;

        var day = DayOf(now);
        var index = day.Lessons.Count + 1;

        _current = new LessonRecord
        {
            Id = NewLessonId(day, now, index),
            Start = now,
            End = now,
            Index = index,
            ClassSize = classSize
        };

        day.Lessons.Add(_current);
        _lastSample = DateTime.MinValue;
    }

    // ---------------- 采样 ----------------

    /// <summary>
    /// 检测线程每帧都会调这里。只做记账，绝不做 IO（IO 在下课和定时器里做）。
    ///
    /// 只有 <see cref="TrackState.Sleeping"/>（确认沉睡）才开新记录；
    /// 疑似、判断不了、被排除的老师全都不会写入。
    /// </summary>
    public void Observe(TrackingReport report, DateTime now, int classSize)
    {
        lock (_gate)
        {
            EnsureLoaded();

            // 已经下课结算过，等下一次 BeginLesson 再记账
            if (_autoPaused) return;

            EnsureLesson(now, classSize);

            if (_current == null) return;

            // 采样断档（休眠 / 检测停了很久）：开着的睡眠段按上一拍收口。
            // 否则一个跨了整夜的段会被算成"睡了 8 小时"。
            if (_lastSample != DateTime.MinValue &&
                (now - _lastSample).TotalMinutes > GapMinutes)
            {
                foreach (var ep in _open.Values)
                {
                    if (ep.End == null) ep.End = _lastSample;
                }

                _open.Clear();
            }

            _current.End = now;

            // ---- 逐个轨迹推进"确认睡着 / 醒来" ----
            var seen = new HashSet<int>();

            foreach (var track in report.Tracks)
            {
                seen.Add(track.Id);

                if (track.IsTeacher) continue;

                // 确认沉睡 + 最近还看得到人，才算"现在正睡着"
                var sleeping = track.State == TrackState.Sleeping &&
                               (now - track.LastSeen).TotalSeconds <= 4;

                if (sleeping)
                {
                    if (_open.ContainsKey(track.Id)) continue;

                    var ep = new SleepEpisode
                    {
                        TrackId = track.Id,
                        Start = track.DoubtSince ?? now,
                        LessonId = _current.Id,
                        IsConfirmed = true,
                        CenterX = track.Box.X + track.Box.Width / 2.0,
                        Confidence = Math.Round(track.SleepConfidence, 3),
                        PostureScore = Math.Round(track.PostureScore, 3),
                        OcclusionScore = Math.Round(track.OcclusionScore, 3)
                    };

                    _open[track.Id] = ep;
                    _current.Episodes.Add(ep);
                    _dirty = true;
                }
                else if (_open.Remove(track.Id, out var ep))
                {
                    ep.End = now;
                    _dirty = true;
                }
            }

            // 人离开画面：把还开着的这段就地封口
            foreach (var id in _open.Keys.ToList())
            {
                if (seen.Contains(id)) continue;

                var track = report.Tracks.FirstOrDefault(t => t.Id == id);
                if (track == null || (now - track.LastSeen).TotalSeconds > 4)
                {
                    if (_open[id].End == null) _open[id].End = now;
                    _open.Remove(id);
                    _dirty = true;
                }
            }

            // ---- 曲线采样，20 秒一个点 ----
            if ((now - _lastSample).TotalSeconds >= SampleSeconds || _lastSample == DateTime.MinValue)
            {
                _lastSample = now;

                _current.Curve.Add(new CurvePoint
                {
                    OffsetSeconds = (int)Math.Max(0, (now - _current.Start).TotalSeconds),
                    Sleeping = report.Sleeping,
                    Present = report.Present,
                    Doubtful = report.Doubtful,
                    HeadUpRate = Math.Round(report.HeadUpRate * 100, 1)
                });

                if (report.Sleeping > _current.PeakSleeping) _current.PeakSleeping = report.Sleeping;
                _dirty = true;
            }
        }
    }

    /// <summary>把快照路径补到刚写下的那一段上。检测线程抓完图后调。</summary>
    public void AttachSnapshot(int trackId, DateTime start, string relativePath)
    {
        lock (_gate)
        {
            if (_current == null) return;

            var ep = _current.Episodes
                .OrderByDescending(e => e.Start)
                .FirstOrDefault(e => e.TrackId == trackId && Math.Abs((e.Start - start).TotalSeconds) < 5);

            if (ep == null) return;

            ep.SnapshotPath = relativePath;
            _dirty = true;
        }
    }

    /// <summary>定时落盘，防止程序被强行结束丢掉一整节课。</summary>
    public void FlushIfDirty()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            SaveLocked();
        }
    }

    // ---------------- 查询 ----------------

    /// <summary>取最近 n 天的记录，最新的排在最前面。今天永远有一条（哪怕是空的）。</summary>
    public List<DayRecord> RecentDays(int count, DateTime today)
    {
        lock (_gate)
        {
            EnsureLoaded();

            var result = new List<DayRecord>();

            for (var i = 0; i < count; i++)
            {
                var date = today.Date.AddDays(-i);
                var existing = _days.FirstOrDefault(d => d.Date == date);

                // 今天还没数据也要占一页，否则总结第一页会是空的
                result.Add(existing ?? new DayRecord { Date = date });
            }

            return result;
        }
    }

    /// <summary>按课程 ID 精确取一节课。课后总结用它，杜绝串档。</summary>
    public LessonRecord? GetLesson(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        lock (_gate)
        {
            EnsureLoaded();

            foreach (var day in _days)
            {
                foreach (var lesson in day.Lessons)
                {
                    if (lesson.Id == id) return lesson;
                }
            }

            return null;
        }
    }

    /// <summary>直接把今天这一页拿出来（含正在上的那节）。</summary>
    public DayRecord TodaySnapshot(DateTime now)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var day = DayOf(now);
            return day;
        }
    }

    private DayRecord DayOf(DateTime now)
    {
        var date = now.Date;
        var day = _days.FirstOrDefault(d => d.Date == date);

        if (day == null)
        {
            day = new DayRecord { Date = date };
            _days.Add(day);
        }

        return day;
    }

    // ---------------- 持久化 ----------------

    private static string FilePath => Path.Combine(Plugin.ConfigFolder, "history.json");

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;

            var json = File.ReadAllText(FilePath);
            var days = JsonSerializer.Deserialize<List<DayRecord>>(json, JsonOptions);
            if (days == null) return;

            _days.Clear();
            _days.AddRange(days.OrderBy(d => d.Date));

            Migrate();
        }
        catch (Exception ex)
        {
            AppLog.Warn("历史记录读取失败，按空账本继续：" + ex.Message);
        }
    }

    /// <summary>
    /// 老数据补齐。1.4 的记录没有课程 ID，也没有确认标记：
    /// 补成「未分类课程」并当作确认记录，升级后历史照常能看。
    /// </summary>
    private void Migrate()
    {
        var changed = false;

        foreach (var day in _days)
        {
            foreach (var lesson in day.Lessons)
            {
                if (string.IsNullOrWhiteSpace(lesson.Id))
                {
                    lesson.Id = $"legacy-{day.Date:yyyyMMdd}-{lesson.Index}-{lesson.Start:HHmm}";
                    changed = true;
                }

                foreach (var ep in lesson.Episodes)
                {
                    if (string.IsNullOrWhiteSpace(ep.LessonId))
                    {
                        ep.LessonId = lesson.Id;
                        changed = true;
                    }

                    if (!ep.IsConfirmed)
                    {
                        // 老版本只会在判定睡着时写入，所以历史记录一律按确认处理
                        ep.IsConfirmed = true;
                        changed = true;
                    }
                }
            }
        }

        if (changed) _dirty = true;
    }

    private void SaveLocked()
    {
        try
        {
            var cutoff = DateTime.Today.AddDays(-KeepDays);
            _days.RemoveAll(d => d.Date < cutoff);

            Directory.CreateDirectory(Plugin.ConfigFolder);

            // 先写临时文件再替换，避免写一半断电把历史写坏
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_days, JsonOptions));
            File.Copy(tmp, FilePath, true);
            File.Delete(tmp);

            _dirty = false;
        }
        catch (Exception ex)
        {
            AppLog.Error("历史记录写入失败", ex);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
}
