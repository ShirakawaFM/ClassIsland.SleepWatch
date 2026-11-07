using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ClassIsland.SleepWatch.Services;

// ============================================================================
// 静默检测的数据模型：一个人一次睡眠、一节课、一天。
// 单独放一个文件，界面和统计代码只依赖这些类型，不依赖账本本身。
//
// 兼容性：1.5 新增的字段全部带默认值，1.4 的 history.json 直接能读；
// 读取时会把没有课程 ID 的老记录补成「未分类课程」，不丢数据。
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

    /// <summary>
    /// 所属课程。1.5 之前没有这个字段，读进来是空串，会归到「未分类课程」。
    /// 有了它，每节课的数据才能独立查询，不会串档。
    /// </summary>
    public string LessonId { get; set; } = "";

    /// <summary>是否为「确认沉睡」。老记录一律按确认处理，否则升级后历史会凭空消失。</summary>
    public bool IsConfirmed { get; set; } = true;

    /// <summary>确认时刻的综合置信度 0~1。</summary>
    public double Confidence { get; set; }

    /// <summary>证据快照的相对路径（相对快照根目录）。没开快照就是空串。</summary>
    public string SnapshotPath { get; set; } = "";

    /// <summary>入睡时的姿态分值 / 遮挡分值，事后调参用。</summary>
    public double PostureScore { get; set; }

    public double OcclusionScore { get; set; }

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

    /// <summary>抬头率 0~100。老数据没有这个值，画图时当作缺省跳过。</summary>
    public double HeadUpRate { get; set; }

    /// <summary>这一拍疑似（计时中）的人数。</summary>
    public int Doubtful { get; set; }
}

/// <summary>一节课的完整记录。</summary>
public sealed class LessonRecord
{
    /// <summary>课程唯一标识。日期 + 节次 + 科目，跨天也不会撞。</summary>
    public string Id { get; set; } = "";

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

    /// <summary>这节课是不是手动模拟出来的。</summary>
    public bool Manual { get; set; }

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

    /// <summary>平均抬头率（0~100）。没有任何采样点时返回 -1。</summary>
    [JsonIgnore]
    public double AverageHeadUpRate
    {
        get
        {
            var points = Curve.Where(p => p.Present > 0).ToList();
            return points.Count == 0 ? -1 : points.Average(p => p.HeadUpRate);
        }
    }

    /// <summary>最低抬头率（0~100）。没有采样点时返回 -1。</summary>
    [JsonIgnore]
    public double MinHeadUpRate
    {
        get
        {
            var points = Curve.Where(p => p.Present > 0).ToList();
            return points.Count == 0 ? -1 : points.Min(p => p.HeadUpRate);
        }
    }

    /// <summary>有快照的睡眠段。</summary>
    [JsonIgnore]
    public List<SleepEpisode> Snapshots => Episodes.Where(e => !string.IsNullOrEmpty(e.SnapshotPath)).ToList();
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

    /// <summary>全天平均抬头率（0~100）。</summary>
    [JsonIgnore]
    public double AverageHeadUpRate
    {
        get
        {
            var points = Lessons.SelectMany(l => l.Curve).Where(p => p.Present > 0).ToList();
            return points.Count == 0 ? -1 : points.Average(p => p.HeadUpRate);
        }
    }
}
