using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassIsland.SleepWatch.Services;

/// <summary>大数字卡片。</summary>
public sealed class StatCard
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Hint { get; init; } = "";
    public bool Enabled { get; init; } = true;
}

/// <summary>幻灯片里的一页 = 一天。</summary>
public sealed class SummaryPage
{
    public DateTime Date { get; init; }
    public bool IsToday { get; init; }
    public bool HasData { get; init; }

    public string DayLabel { get; init; } = "";
    public string LessonLabel { get; init; } = "";
    public string Headline { get; init; } = "";
    public string Subline { get; init; } = "";
    public string FooterNote { get; init; } = "";

    public List<StatCard> Stats { get; init; } = new();
    public List<CurvePoint> Curve { get; init; } = new();
    public List<FactItem> Facts { get; init; } = new();

    /// <summary>曲线横轴两点的时间文本。</summary>
    public string CurveStart { get; init; } = "";
    public string CurveEnd { get; init; } = "";
    public int Peak { get; init; }
}

/// <summary>把账本里的一天，折成一页可以直接往界面上摆的东西。</summary>
public static class SummaryBuilder
{
    /// <summary>曲线最多画这么多根柱子，再多屏幕上也看不清。</summary>
    private const int MaxBars = 46;

    public static SummaryPage Build(
        DayRecord day,
        IReadOnlyList<DayRecord> history,
        SummarySettings cfg,
        int classSize,
        double thresholdSeconds)
    {
        var isToday = day.Date == DateTime.Today;
        var dayLabel = DayLabel(day.Date, isToday);

        var lesson = PickLesson(day);

        if (lesson == null)
        {
            return new SummaryPage
            {
                Date = day.Date,
                IsToday = isToday,
                HasData = false,
                DayLabel = dayLabel,
                Headline = isToday ? "还没有记录" : "这天没有课",
                Subline = isToday
                    ? "上完一节课，下课铃一响就会自动出报告。"
                    : "这天没有监测记录，可能没上课或者插件没开。",
                FooterNote = "",
                Stats = EmptyStats(cfg),
                Facts = new List<FactItem>
                {
                    new() { Tag = "—", Text = isToday ? "先让摄像头认认真真看一节课。" : "翻到有记录的那天看看。" }
                }
            };
        }

        var stats = new List<StatCard>
        {
            new()
            {
                Label = "睡觉总人数",
                Value = lesson.Sleepers.ToString(),
                Hint = $"本节 · 应到 {classSize} 人",
                Enabled = cfg.ShowTotalSleepers
            },
            new()
            {
                Label = "已睡醒",
                Value = lesson.Awakenings.ToString(),
                Hint = lesson.Episodes.Count == 0 ? "没有睡着的记录" : $"占睡着的 {Ratio(lesson.Awakenings, lesson.Episodes.Count)}",
                Enabled = cfg.ShowAwakened
            },
            new()
            {
                Label = "同一人入睡次数",
                Value = lesson.MaxRepeat > 0 ? $"{lesson.MaxRepeat} 次" : "0 次",
                Hint = lesson.MaxRepeat > 1 ? "睡醒又睡，记在同一位置上" : "没有人睡两轮",
                Enabled = cfg.ShowRepeat
            },
            new()
            {
                Label = "最长单次睡眠",
                Value = lesson.Longest != null ? FunFactWriter.Duration(lesson.Longest.Seconds) : "—",
                Hint = lesson.Longest != null ? $"≈ 刷 {(int)Math.Max(1, Math.Round(lesson.Longest.Seconds / 480.0))} 集泡面番" : "—",
                Enabled = cfg.ShowLongest
            }
        };

        if (cfg.ShowPeak)
        {
            stats.Add(new StatCard
            {
                Label = "同时最多",
                Value = $"{lesson.PeakSleeping} 人",
                Hint = lesson.PeakSleeping > 0 ? "一节课的最高水位" : "没有人睡着",
                Enabled = true
            });
        }

        var facts = cfg.ShowFacts
            ? FunFactWriter.Build(lesson, day, history, classSize, thresholdSeconds)
            : new List<FactItem>();

        var curve = Downsample(lesson.Curve);

        return new SummaryPage
        {
            Date = day.Date,
            IsToday = isToday,
            HasData = true,
            DayLabel = dayLabel,
            LessonLabel = lesson.Title,
            Headline = isToday
                ? $"这节课，有 {lesson.Sleepers} 个人睡着了"
                : $"这天，有 {lesson.Sleepers} 个人睡着了",
            Subline = $"记录时长 {FunFactWriter.Duration(lesson.Length.TotalSeconds)} · 采样 {lesson.Curve.Count} 次 · 判定线 {FunFactWriter.Duration(thresholdSeconds)}",
            FooterNote = day.Lessons.Count > 1
                ? $"当天共 {day.Lessons.Count} 节 · 累计 {day.TotalEpisodes} 人次 · {day.TotalMinutes:0} 分钟"
                : "",
            Stats = stats,
            Curve = curve,
            Facts = facts,
            CurveStart = lesson.Start.ToString("HH:mm"),
            CurveEnd = lesson.End.ToString("HH:mm"),
            Peak = lesson.PeakSleeping
        };
    }

    /// <summary>一天里挑一节最有话说的：睡的人最多那节，一样多就取靠后的。</summary>
    private static LessonRecord? PickLesson(DayRecord day)
    {
        if (day.Lessons.Count == 0) return null;

        return day.Lessons
            .OrderByDescending(l => l.Episodes.Count)
            .ThenByDescending(l => l.Start)
            .FirstOrDefault();
    }

    private static List<StatCard> EmptyStats(SummarySettings cfg) => new()
    {
        new() { Label = "睡觉总人数", Value = "—", Hint = "", Enabled = cfg.ShowTotalSleepers },
        new() { Label = "已睡醒", Value = "—", Hint = "", Enabled = cfg.ShowAwakened },
        new() { Label = "同一人入睡次数", Value = "—", Hint = "", Enabled = cfg.ShowRepeat },
        new() { Label = "最长单次睡眠", Value = "—", Hint = "", Enabled = cfg.ShowLongest }
    };

    /// <summary>把几百个采样点压到几十根柱子，取每段里的最大值，峰值不会被抹平。</summary>
    private static List<CurvePoint> Downsample(List<CurvePoint> source)
    {
        if (source.Count <= MaxBars) return source.ToList();

        var result = new List<CurvePoint>(MaxBars);
        var size = source.Count / (double)MaxBars;

        for (var i = 0; i < MaxBars; i++)
        {
            var from = (int)Math.Floor(i * size);
            var to = Math.Min(source.Count, (int)Math.Floor((i + 1) * size));
            if (to <= from) continue;

            var chunk = source.Skip(from).Take(to - from).ToList();

            result.Add(new CurvePoint
            {
                OffsetSeconds = chunk[0].OffsetSeconds,
                Sleeping = chunk.Max(p => p.Sleeping),
                Present = chunk.Max(p => p.Present)
            });
        }

        return result;
    }

    private static string Ratio(int a, int b) => b <= 0 ? "—" : $"{a * 100 / b}%";

    private static string DayLabel(DateTime date, bool isToday)
    {
        var week = "日一二三四五六"[(int)date.DayOfWeek];
        var body = $"{date.Month} 月 {date.Day} 日 · 周{week}";

        if (isToday) return "今天 · " + body;

        var days = (DateTime.Today - date.Date).Days;
        var prefix = days switch
        {
            1 => "昨天 · ",
            2 => "前天 · ",
            _ => $"前 {days} 天 · "
        };

        return prefix + body;
    }
}

/// <summary>
/// 示例数据。配置面板里"看一眼效果"用，不写进 history.json。
/// </summary>
public static class SampleData
{
    public static List<DayRecord> BuildDays(int count, int seed = 20261031)
    {
        var random = new Random(seed);
        var days = new List<DayRecord>();

        for (var d = 0; d < count; d++)
        {
            var date = DateTime.Today.AddDays(-d);
            var day = new DayRecord { Date = date };

            var lessons = 4 + random.Next(0, 3);

            for (var i = 0; i < lessons; i++)
            {
                var start = date.AddHours(8).AddMinutes(i * 55 + random.Next(0, 8));
                var along = 38 + random.Next(0, 8);

                var lesson = new LessonRecord
                {
                    Start = start,
                    End = start.AddMinutes(along),
                    Index = i + 1,
                    ClassSize = 45
                };

                // 越到下午越容易困：基础人数按时段递增
                var baseCount = d == 0 && i == lessons - 1
                    ? 6 + random.Next(0, 3)
                    : 1 + i + random.Next(0, 3);

                var sleeping = 0;

                for (var t = 0; t <= along; t++)
                {
                    lesson.Curve.Add(new CurvePoint
                    {
                        OffsetSeconds = t * 60,
                        Sleeping = sleeping,
                        Present = 44
                    });

                    if (random.NextDouble() < 0.10) sleeping = Math.Min(baseCount, sleeping + 1);
                    if (random.NextDouble() < 0.07) sleeping = Math.Max(0, sleeping - 1);
                }

                lesson.PeakSleeping = lesson.Curve.Count > 0 ? lesson.Curve.Max(p => p.Sleeping) : 0;

                for (var k = 0; k < baseCount + random.Next(0, 2); k++)
                {
                    var offsetMin = random.Next(1, along - 2);
                    var lengthMin = Math.Clamp(2 + random.NextDouble() * 16, 1, along - offsetMin);

                    var ep = new SleepEpisode
                    {
                        TrackId = 100 + d * 50 + i * 10 + k,
                        Start = start.AddMinutes(offsetMin),
                        CenterX = 40 + random.Next(0, 560)
                    };

                    // 没醒的那部分统一按"睡到下课"收口，否则统计会把跨天的时间也算进去
                    ep.End = random.NextDouble() < 0.72
                        ? ep.Start.AddMinutes(lengthMin)
                        : lesson.End;

                    lesson.Episodes.Add(ep);
                }

                day.Lessons.Add(lesson);
            }

            days.Add(day);
        }

        return days;
    }
}
