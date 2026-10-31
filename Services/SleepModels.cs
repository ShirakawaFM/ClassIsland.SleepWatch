using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ClassIsland.SleepWatch.Services;

// ============================================================================
// 静默检测的数据模型：一个人一次睡眠、一节课、一天。
// 单独放一个文件，界面和统计代码只依赖这些类型，不依赖账本本身。
// ============================================================================

/// <summary>一次完整的"睡着 → 醒来"。</summary>
public sealed class SleepEpisode
{
    /// <summary>轨迹编号。同一个座位在换人之前基本保持不变。</summary>
    public int TrackId { get; set; }

    public DateTime Start { get; set; }

    /// <summary>结束时刻。还没醒的话是 null。</summary>
    public DateTime? End { get; set; }

    /// <summary>入睡时人体框中心的横坐标（推理图坐标），用来算"画面哪半边更容易困"。</summary>
    public double CenterX { get; set; }

    public double Seconds => (End ?? DateTime.Now).Subtract(Start).TotalSeconds;

    [JsonIgnore]
    public bool Awake => End != null;
}

/// <summary>人数曲线上的一个点。</summary>
public sealed class CurvePoint
{
    /// <summary>距离这节课开始的秒数。</summary>
    public int OffsetSeconds { get; set; }

    public int Sleeping { get; set; }

    public int Present { get; set; }
}

/// <summary>一节课的完整记录。</summary>
public sealed class LessonRecord
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }

    /// <summary>当天第几节（从 1 开始）。拿不到课表时按"当天第几段"算。</summary>
    public int Index { get; set; }

    /// <summary>科目名。课表没配就一直空着，界面退化成只显示「第 N 节」。</summary>
    public string Subject { get; set; } = "";

    public int ClassSize { get; set; }

    public List<SleepEpisode> Episodes { get; set; } = new();

    public List<CurvePoint> Curve { get; set; } = new();

    public int PeakSleeping { get; set; }

    public TimeSpan Length => End > Start ? End - Start : TimeSpan.Zero;

    [JsonIgnore]
    public string Title
    {
        get
        {
            var head = Index > 0 ? $"第 {Index} 节" : "";
            if (!string.IsNullOrWhiteSpace(Subject)) head = head.Length > 0 ? $"{head} · {Subject}" : Subject;
            if (head.Length == 0) return $"{Start:HH:mm}–{End:HH:mm}";

            return $"{head} · {Start:HH:mm}–{End:HH:mm}";
        }
    }

    /// <summary>睡过的人数（同一个人睡两次也只算一个人）。</summary>
    [JsonIgnore]
    public int Sleepers => Episodes.Select(e => e.TrackId).Distinct().Count();

    /// <summary>醒过来的人数（同一个人睡醒又睡，只算一个；不会超过睡过的总人数）。</summary>
    [JsonIgnore]
    public int Awakenings => Episodes.Where(e => e.Awake).Select(e => e.TrackId).Distinct().Count();

    /// <summary>同一位置最多睡了几次。</summary>
    [JsonIgnore]
    public int MaxRepeat => Episodes.GroupBy(e => e.TrackId).Select(g => g.Count()).DefaultIfEmpty(0).Max();

    [JsonIgnore]
    public SleepEpisode? Longest =>
        Episodes.OrderByDescending(e => e.Seconds).FirstOrDefault();
}

/// <summary>一天的记录。</summary>
public sealed class DayRecord
{
    /// <summary>日期（只取年月日）。</summary>
    public DateTime Date { get; set; }

    public List<LessonRecord> Lessons { get; set; } = new();

    [JsonIgnore]
    public int TotalSleepers => Lessons.SelectMany(l => l.Episodes).Select(e => e.TrackId).Distinct().Count();

    [JsonIgnore]
    public int TotalEpisodes => Lessons.Sum(l => l.Episodes.Count);

    [JsonIgnore]
    public double TotalMinutes => Lessons.SelectMany(l => l.Episodes).Sum(e => e.Seconds) / 60.0;

    [JsonIgnore]
    public SleepEpisode? Longest =>
        Lessons.SelectMany(l => l.Episodes).OrderByDescending(e => e.Seconds).FirstOrDefault();
}
