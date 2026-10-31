using System;
using System.Collections.Generic;

using OpenCvSharp;

namespace ClassIsland.SleepWatch.Services;

public enum TrackState
{
    /// <summary>看到了人，但既没判定出睁眼，也没看到低头 —— 可能在转头、被挡住。</summary>
    Unknown,

    /// <summary>清醒。</summary>
    Awake,

    /// <summary>疑似睡着。</summary>
    Sleeping
}

/// <summary>一个人（同一位置持续出现的目标）的状态记录。</summary>
public sealed class PersonTrack
{
    public int Id { get; init; }

    public Rect Box { get; set; }

    public DateTime FirstSeen { get; init; }

    public DateTime LastSeen { get; set; }

    /// <summary>最后一次确认"睁着眼"的时刻。</summary>
    public DateTime LastAwake { get; set; }

    /// <summary>最后一次能可靠判断眼部的时刻（脸够清楚）。</summary>
    public DateTime LastEyeEvidence { get; set; }

    /// <summary>什么时候开始持续低头的。抬头就清空。</summary>
    public DateTime? HeadDownSince { get; set; }

    public bool HeadDown { get; set; }

    public bool EyesOpen { get; set; }

    /// <summary>这个目标是否曾经被判定过（否则只能说"未知"）。</summary>
    public bool EverJudged { get; set; }

    public TrackState State { get; set; } = TrackState.Unknown;
}

/// <summary>一帧结算的结果。</summary>
public sealed class TrackingReport
{
    public int Present { get; set; }
    public int Awake { get; set; }
    public int Sleeping { get; set; }
    public int Unknown { get; set; }
    public List<PersonTrack> Tracks { get; } = new();
}

/// <summary>
/// 把人脸和人体两部分观测合并成"人"的轨迹，并维护每个人的计时器。
///
/// 这里没有身份识别，也没有座位标定：靠前后帧的框重叠把同一个人的状态串起来。
/// 人走了、被挡住了，轨迹过一会自然消失，不会残留。
/// </summary>
public sealed class PersonTracker
{
    private readonly List<PersonTrack> _tracks = new();
    private int _nextId = 1;

    /// <summary>超过这个时长没再出现，就不算"在座"了。</summary>
    private const double ActiveSeconds = 4.0;

    /// <summary>眼睛信号的有效期：超过这个时间没有新的睁眼证据，就不能再用"闭眼"推断睡着。</summary>
    private const double EyeEvidenceSeconds = 2.5;

    public void Reset() => _tracks.Clear();

    public TrackingReport Update(
        List<PosePerson> persons,
        DateTime now,
        DetectionSettings cfg)
    {
        var observations = BuildObservations(persons);

        MatchAndUpdate(observations, now, cfg);

        _tracks.RemoveAll(t => (now - t.LastSeen).TotalMinutes > cfg.PresenceMinutes);

        var report = new TrackingReport();

        foreach (var track in _tracks)
        {
            report.Tracks.Add(track);

            if ((now - track.LastSeen).TotalSeconds > ActiveSeconds) continue;

            report.Present++;

            switch (track.State)
            {
                case TrackState.Sleeping:
                    report.Sleeping++;
                    break;
                case TrackState.Awake:
                    report.Awake++;
                    break;
                default:
                    report.Unknown++;
                    break;
            }
        }

        return report;
    }

    // ---------- 观测合并 ----------

    private sealed class Observation
    {
        public Rect Box;
        public bool HeadDown;
        public bool HeadJudgeable;
        public bool EyesJudgeable;
        public bool EyesOpen;
    }

    private static List<Observation> BuildObservations(List<PosePerson> persons)
    {
        var list = new List<Observation>(persons.Count);

        foreach (var person in persons)
        {
            list.Add(new Observation
            {
                Box = person.Box,
                HeadDown = person.HeadDown,
                HeadJudgeable = person.HeadJudgeable,
                EyesJudgeable = person.EyesJudgeable,
                EyesOpen = person.EyesOpen
            });
        }

        return list;
    }

    // ---------- 关联与状态推进 ----------

    private void MatchAndUpdate(List<Observation> observations, DateTime now, DetectionSettings cfg)
    {
        var used = new bool[_tracks.Count];

        foreach (var obs in observations)
        {
            var index = FindMatch(obs.Box, used);

            if (index < 0)
            {
                var track = new PersonTrack
                {
                    Id = _nextId++,
                    Box = obs.Box,
                    FirstSeen = now,
                    LastSeen = now,
                    LastAwake = now,
                    LastEyeEvidence = DateTime.MinValue
                };

                Apply(track, obs, now, cfg);
                _tracks.Add(track);

                // 新轨迹本帧已被占用，别让同帧的另一个框再匹配到它
                Array.Resize(ref used, _tracks.Count);
                used[used.Length - 1] = true;
                continue;
            }

            used[index] = true;
            var target = _tracks[index];
            target.Box = obs.Box;
            target.LastSeen = now;

            Apply(target, obs, now, cfg);
        }
    }

    private int FindMatch(Rect box, bool[] used)
    {
        var best = -1;
        var bestScore = 0f;

        for (var i = 0; i < _tracks.Count; i++)
        {
            if (used[i]) continue;

            var iou = Iou(_tracks[i].Box, box);
            if (iou > bestScore)
            {
                bestScore = iou;
                best = i;
            }
        }

        if (bestScore >= 0.2f) return best;

        // 框重叠不够时，再看中心距离（人轻微移动时 IoU 会掉得很快）
        var center = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        var bestDist = double.MaxValue;
        var near = -1;

        for (var i = 0; i < _tracks.Count; i++)
        {
            if (used[i]) continue;

            var tc = new Point(_tracks[i].Box.X + _tracks[i].Box.Width / 2,
                _tracks[i].Box.Y + _tracks[i].Box.Height / 2);

            var dx = tc.X - center.X;
            var dy = tc.Y - center.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            var limit = Math.Max(box.Width, _tracks[i].Box.Width) * 0.6;

            if (dist <= limit && dist < bestDist)
            {
                bestDist = dist;
                near = i;
            }
        }

        return near;
    }

    private static void Apply(PersonTrack track, Observation obs, DateTime now, DetectionSettings cfg)
    {
        track.HeadDown = obs.HeadDown;

        if (obs.EyesJudgeable)
        {
            track.LastEyeEvidence = now;
            track.EyesOpen = obs.EyesOpen;

            if (obs.EyesOpen)
            {
                track.LastAwake = now;
                track.EverJudged = true;
            }
        }
        else
        {
            track.EyesOpen = false;
        }

        if (obs.HeadJudgeable && obs.HeadDown)
        {
            track.HeadDownSince ??= now;
            track.EverJudged = true;
        }
        else if (obs.HeadJudgeable)
        {
            track.HeadDownSince = null;
        }
        else
        {
            // 姿态看不出来（背对 / 被挡）：计时器暂停，宁可少报不误报
        }

        var sleepSeconds = Math.Max(2, cfg.SleepSeconds);

        // 条件一：刚才还看得清他的眼睛，而且已经连续 N 秒没抓到睁眼了
        var eyeFresh = (now - track.LastEyeEvidence).TotalSeconds <= EyeEvidenceSeconds;
        var sleepByEye = eyeFresh && (now - track.LastAwake).TotalSeconds > sleepSeconds;

        // 条件二：头一直低着 / 伏在桌上超过 N 秒
        var sleepByPosture = track.HeadDownSince is { } since &&
                             (now - since).TotalSeconds > sleepSeconds;

        track.State = sleepByEye || sleepByPosture
            ? TrackState.Sleeping
            : track.EverJudged ? TrackState.Awake : TrackState.Unknown;
    }

    private static float Iou(Rect a, Rect b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

        var w = x2 - x1;
        var h = y2 - y1;
        if (w <= 0 || h <= 0) return 0f;

        var inter = w * h;
        var union = a.Width * (float)a.Height + b.Width * (float)b.Height - inter;
        return union <= 0 ? 0f : inter / union;
    }
}
