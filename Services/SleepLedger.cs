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
// 检测线程每帧都会把"谁在睡 / 谁醒了"喂进来，这里把它记成一段一段的睡眠事件。
// 全程不弹任何东西、不改主界面，就是安静地记账 —— 下课铃一响，账本交给课后总结。
//
// 所有写入都走 _gate：采样在检测线程，结算在 UI 线程，两边都会碰这几个集合。
// ============================================================================

/// <summary>
/// 账本本体。单例，检测线程和 UI 线程都会碰，内部全部加锁。
/// </summary>
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
    public void BeginLesson(DateTime now, int classSize, int index = 0, string subject = "")
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
                Start = now,
                End = now,
                Index = index,
                Subject = subject ?? "",
                ClassSize = classSize
            };

            day.Lessons.Add(_current);
            _open.Clear();
            _lastSample = DateTime.MinValue;
        }
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
        catch
        {
            // 事件里出错不能影响检测线程
        }

        return finished;
    }

    private void CloseCurrent(DateTime now)
    {
        if (_current == null) return;

        foreach (var ep in _open.Values.ToList())
        {
            ep.End = now;
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

            // ---- 逐个轨迹推进"睡着 / 醒来" ----
            var seen = new HashSet<int>();

            foreach (var track in report.Tracks)
            {
                seen.Add(track.Id);

                var sleeping = track.State == TrackState.Sleeping &&
                               (now - track.LastSeen).TotalSeconds <= 4;

                if (sleeping)
                {
                    if (!_open.ContainsKey(track.Id))
                    {
                        var ep = new SleepEpisode
                        {
                            TrackId = track.Id,
                            Start = now,
                            CenterX = track.Box.X + track.Box.Width / 2.0
                        };

                        _open[track.Id] = ep;
                        _current.Episodes.Add(ep);
                        _dirty = true;
                    }
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
                    _open[id].End = now;
                    _open.Remove(id);
                    _dirty = true;
                }
            }

            // ---- 曲线采样，20 秒一个点 ----
            if ((now - _lastSample).TotalSeconds >= SampleSeconds || _lastSample == DateTime.MinValue)
            {
                _lastSample = now;

                var point = new CurvePoint
                {
                    OffsetSeconds = (int)Math.Max(0, (now - _current.Start).TotalSeconds),
                    Sleeping = report.Sleeping,
                    Present = report.Present
                };

                _current.Curve.Add(point);
                if (report.Sleeping > _current.PeakSleeping) _current.PeakSleeping = report.Sleeping;
            }
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
        }
        catch
        {
            // 历史损坏就当没有，不影响上课
        }
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
        catch
        {
            // 写盘失败不抛，下节课还会再试
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
}
