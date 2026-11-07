using System;
using System.Collections.Generic;

using OpenCvSharp;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 一个目标的判定状态。
///
/// 四层结构，只有 <see cref="Sleeping"/> 才会写进账本：
/// <list type="bullet">
/// <item><see cref="Awake"/> 清醒 —— 默认状态，看到睁眼或者有动作就是它；</item>
/// <item><see cref="Doubtful"/> 疑似沉睡（计时中）—— 连续静置够久，但还没到准入时长；</item>
/// <item><see cref="Sleeping"/> 确认沉睡 —— 疑似再撑满准入时长，这一下才记账、才抓拍；</item>
/// <item><see cref="Unknown"/> 判断失效 —— 被挡、关键点全丢，不计入任何统计。</item>
/// </list>
/// 另外 <see cref="Excluded"/> 是「非学生」（站着的老师），单独一类，不进学生统计。
/// </summary>
public enum TrackState
{
    Unknown,
    Awake,
    Doubtful,
    Sleeping,
    Excluded
}

/// <summary>一个人（同一位置持续出现的目标）的完整状态。</summary>
public sealed class PersonTrack
{
    public int Id { get; init; }

    public Rect Box { get; set; }

    public DateTime FirstSeen { get; init; }

    public DateTime LastSeen { get; set; }

    public TrackState State { get; set; } = TrackState.Unknown;

    /// <summary>这个目标是否曾经得出过结论（否则只能说"未知"）。</summary>
    public bool EverJudged { get; set; }

    // ---------------- 姿态与眼部 ----------------

    public bool HeadDown { get; set; }

    public bool HeadJudgeable { get; set; }

    public bool EyesJudgeable { get; set; }

    public bool EyesOpen { get; set; }

    /// <summary>最后一次真的看到"睁着眼"的时刻。用来看眼部信号有没有过期。</summary>
    public DateTime LastEyeOpen { get; set; }

    /// <summary>最后一次能可靠判断眼部的时刻（脸够清楚）。</summary>
    public DateTime LastEyeEvidence { get; set; }

    // ---------------- 静置计时 ----------------

    /// <summary>最后一次有效动作的时刻。这是新判定规则的核心。</summary>
    public DateTime LastMotion { get; set; }

    /// <summary>什么时候从清醒转入疑似的。转入确认沉睡要在这个基础上再等准入时长。</summary>
    public DateTime? DoubtSince { get; set; }

    /// <summary>本帧刚由疑似跃迁为确认沉睡。检测引擎用它触发一次快照。</summary>
    public bool JustConfirmed { get; set; }

    /// <summary>本帧刚由确认沉睡退回清醒。账本用它闭合这段记录。</summary>
    public bool JustWoke { get; set; }

    // ---------------- 多维度指标（预览悬浮提示 / 调参用） ----------------

    /// <summary>姿态分值 0~1：1 = 完全趴桌低头，0 = 正常抬头。</summary>
    public double PostureScore { get; set; }

    /// <summary>埋头遮挡分值 0~1：越高代表脸被挡得越狠。</summary>
    public double OcclusionScore { get; set; }

    /// <summary>连续静置时长（秒）。</summary>
    public double StillSeconds { get; set; }

    /// <summary>睁眼置信度 0~1。</summary>
    public double EyeOpenScore { get; set; }

    /// <summary>综合沉睡置信度 0~1。</summary>
    public double SleepConfidence { get; set; }

    /// <summary>当前陷入沉睡所需的静置时长（已按姿态加权折算）。</summary>
    public double RequiredSeconds { get; set; }

    // ---------------- 轨迹统计 ----------------

    public double AwakeSeconds { get; set; }

    public double SleepingSeconds { get; set; }

    public int SleepCount { get; set; }

    // ---------------- 运动特征 ----------------

    /// <summary>预测位置用的速度（像素/秒）。</summary>
    public double VelocityX { get; set; }

    public double VelocityY { get; set; }

    /// <summary>角色判定窗口内累计的位移，用来区分"走来走去的老师"和"坐着不动的学生"。</summary>
    public double MotionAccum { get; set; }

    public DateTime LastRoleCheck { get; set; }

    /// <summary>髋部关键点是否可见。可见基本意味着人站着（坐着时下半身被课桌挡住）。</summary>
    public bool HipsVisible { get; set; }

    /// <summary>被判成非学生（老师）。一旦判上就保持到轨迹消失，避免统计数字来回跳。</summary>
    public bool IsTeacher { get; set; }

    public int StandingHits { get; set; }

    public int MotionHits { get; set; }

    public int RegionHits { get; set; }

    /// <summary>上一帧头部关键点位置，用来判断头部有没有明显动作。</summary>
    public float PrevHeadX { get; set; } = float.NaN;

    public float PrevHeadY { get; set; } = float.NaN;

    public bool HasPrevHead { get; set; }

    /// <summary>预测中心点，匹配时用。</summary>
    public Point2d Predict(double seconds)
        => new(Box.X + Box.Width / 2.0 + VelocityX * seconds,
               Box.Y + Box.Height / 2.0 + VelocityY * seconds);
}

/// <summary>一帧结算的结果。</summary>
public sealed class TrackingReport
{
    /// <summary>画面里找到的学生人数（不含老师）。</summary>
    public int Present { get; set; }

    public int Awake { get; set; }

    /// <summary>确认沉睡人数。只有它会被计入账本。</summary>
    public int Sleeping { get; set; }

    /// <summary>疑似沉睡（计时中）人数。</summary>
    public int Doubtful { get; set; }

    /// <summary>看到了人但判断不了状态（转身、被挡、脸太小）。</summary>
    public int Unknown { get; set; }

    /// <summary>被判定为非学生、排除出统计的人数。</summary>
    public int Excluded { get; set; }

    /// <summary>本帧因为超出应到人数而被丢弃的低置信度目标数。</summary>
    public int Filtered { get; set; }

    /// <summary>抬头率 0~1。分母只算能明确判定状态的学生。</summary>
    public double HeadUpRate { get; set; }

    public List<PersonTrack> Tracks { get; } = new();
}

/// <summary>
/// 把人体观测串成"人"的轨迹，并维护每个人的静置计时与状态机。
///
/// 这里没有身份识别，也没有座位标定：靠前后帧的框重叠、尺寸相似度和运动方向
/// 把同一个人的状态串起来。人真的走了，轨迹过一会自然消失，不会残留。
///
/// 判定规则刻意不绑定固定姿势：<b>连续静置时长是主判据</b>，低头和闭眼只是加权项。
/// 这样坐直了闭眼打盹也能抓到，而低头捡笔不会被算成睡觉。
/// </summary>
public sealed class PersonTracker
{
    private readonly List<PersonTrack> _tracks = new();
    private int _nextId = 1;

    /// <summary>超过这个时长没再出现，就不算"在座"了。</summary>
    private const double ActiveSeconds = 4.0;

    /// <summary>角色判定（老师 / 学生）的刷新周期。</summary>
    private const double RoleCheckSeconds = 5.0;

    public IReadOnlyList<PersonTrack> Tracks => _tracks;

    /// <summary>重建轨迹表。换课、重启检测时调用。</summary>
    public void Reset()
    {
        _tracks.Clear();
        _nextId = 1;
    }

    public TrackingReport Update(
        List<PosePerson> persons,
        DateTime now,
        DetectionSettings cfg,
        int frameWidth,
        int frameHeight)
    {
        var observations = BuildObservations(persons);

        var filtered = ApplyClassLimit(observations, cfg, now);

        MatchAndUpdate(observations, now, cfg);

        foreach (var track in _tracks)
        {
            ClassifyRole(track, now, cfg, frameHeight);
        }

        // 人走了、被挡住了，轨迹过一会自然消失，不会残留
        var aliveSeconds = Math.Max(cfg.PresenceMinutes * 60, cfg.OcclusionSeconds * 2);
        _tracks.RemoveAll(t => (now - t.LastSeen).TotalSeconds > aliveSeconds);

        var report = new TrackingReport { Filtered = filtered };

        foreach (var track in _tracks)
        {
            report.Tracks.Add(track);

            // 超过 ActiveSeconds 没再出现：轨迹还留着（可能只是被挡），但不再计入人数
            if ((now - track.LastSeen).TotalSeconds > ActiveSeconds) continue;

            if (track.IsTeacher)
            {
                report.Excluded++;
                continue;
            }

            report.Present++;

            switch (track.State)
            {
                case TrackState.Sleeping:
                    report.Sleeping++;
                    break;
                case TrackState.Doubtful:
                    report.Doubtful++;
                    break;
                case TrackState.Awake:
                    report.Awake++;
                    break;
                default:
                    report.Unknown++;
                    break;
            }
        }

        // 抬头率：分母只算能明确判定状态的学生，判断不了的不参与
        var judgeable = report.Awake + report.Sleeping + report.Doubtful;
        report.HeadUpRate = judgeable > 0 ? report.Awake / (double)judgeable : 0;

        return report;
    }

    // ---------- 观测合并 ----------

    private sealed class Observation
    {
        public Rect Box;
        public float Score;
        public bool HeadDown;
        public bool HeadJudgeable;
        public bool EyesJudgeable;
        public bool EyesOpen;
        public double EyeScore;
        public double PostureScore;
        public double OcclusionScore;
        public float HeadX;
        public float HeadY;
        public bool HasHead;

        /// <summary>髋部关键点可见（11 左髋 / 12 右髋）。</summary>
        public bool HipsVisible;

        /// <summary>同帧里已经有轨迹认领了它（人数上限过滤时要优先留老人）。</summary>
        public bool HasTrack;
    }

    private static List<Observation> BuildObservations(List<PosePerson> persons)
    {
        var list = new List<Observation>(persons.Count);

        foreach (var person in persons)
        {
            var obs = new Observation
            {
                Box = person.Box,
                Score = person.Score,
                HeadDown = person.HeadDown,
                HeadJudgeable = person.HeadJudgeable,
                EyesJudgeable = person.EyesJudgeable,
                EyesOpen = person.EyesOpen,
                EyeScore = person.EyeScore,
                PostureScore = person.PostureScore,
                OcclusionScore = person.FaceOcclusion,
                HeadX = person.HeadPoint.X,
                HeadY = person.HeadPoint.Y,
                HasHead = person.HasHeadPoint,
                HipsVisible = person.HipsVisible
            };

            list.Add(obs);
        }

        return list;
    }

    /// <summary>
    /// 人数上限收敛。算法容易把背景、椅子、杂物当成人体，检测数超过应到人数时，
    /// 按置信度留下最高的 N 个，其余直接扔掉，不进跟踪。
    /// 已有轨迹的目标加权，避免"误检挤掉真学生"。
    /// </summary>
    private int ApplyClassLimit(List<Observation> observations, DetectionSettings cfg, DateTime now)
    {
        if (cfg.ClassSize <= 0 || observations.Count <= cfg.ClassSize) return 0;

        // 先标出哪些观测能对上老轨迹
        foreach (var obs in observations)
        {
            foreach (var track in _tracks)
            {
                if ((now - track.LastSeen).TotalSeconds > cfg.OcclusionSeconds) continue;
                if (Iou(track.Box, obs.Box) > 0.35f)
                {
                    obs.HasTrack = true;
                    break;
                }
            }
        }

        observations.Sort((a, b) =>
        {
            var sa = a.Score + (a.HasTrack ? 0.15f : 0f);
            var sb = b.Score + (b.HasTrack ? 0.15f : 0f);
            return sb.CompareTo(sa);
        });

        var dropped = observations.Count - cfg.ClassSize;
        observations.RemoveRange(cfg.ClassSize, dropped);

        return dropped;
    }

    // ---------- 关联与状态推进 ----------

    private void MatchAndUpdate(List<Observation> observations, DateTime now, DetectionSettings cfg)
    {
        var used = new bool[_tracks.Count];

        foreach (var obs in observations)
        {
            var index = FindMatch(obs.Box, used, now, cfg);

            if (index < 0)
            {
                var track = new PersonTrack
                {
                    Id = _nextId++,
                    Box = obs.Box,
                    FirstSeen = now,
                    LastSeen = now,
                    LastMotion = now,
                    LastEyeOpen = now,
                    LastEyeEvidence = DateTime.MinValue
                };

                Apply(track, obs, now, cfg, previousBox: obs.Box, previousSeen: now,
                    delta: 0, isNew: true);

                _tracks.Add(track);

                // 新轨迹本帧已被占用，别让同帧的另一个框再匹配到它
                Array.Resize(ref used, _tracks.Count);
                used[used.Length - 1] = true;
                continue;
            }

            used[index] = true;
            var target = _tracks[index];

            var prevBox = target.Box;
            var prevSeen = target.LastSeen;
            var elapsed = (now - prevSeen).TotalSeconds;
            if (elapsed < 0 || elapsed > 5) elapsed = 0;

            target.Box = obs.Box;
            target.LastSeen = now;

            Apply(target, obs, now, cfg, prevBox, prevSeen, elapsed, isNew: false);
        }
    }

    /// <summary>
    /// 关联打分：IoU + 中心距离 + 尺寸相似度 + 运动方向预测。
    /// 只用 IoU 在人员交叉、趴桌变形时掉得很快，会频繁新建 ID，一个人被数成两个人。
    /// </summary>
    private int FindMatch(Rect box, bool[] used, DateTime now, DetectionSettings cfg)
    {
        var center = new Point2d(box.X + box.Width / 2.0, box.Y + box.Height / 2.0);

        var best = -1;
        var bestScore = 0.0;
        var bestIndexDistance = double.MaxValue;

        for (var i = 0; i < _tracks.Count; i++)
        {
            if (used[i]) continue;

            var track = _tracks[i];
            var gap = (now - track.LastSeen).TotalSeconds;

            // 太久没见到的轨迹不参与常规匹配，除非框几乎完全重合（人一直没动）
            if (gap > cfg.OcclusionSeconds && Iou(track.Box, box) < 0.5f) continue;

            var iou = Iou(track.Box, box);

            var tc = new Point2d(track.Box.X + track.Box.Width / 2.0, track.Box.Y + track.Box.Height / 2.0);
            var dist = Distance(tc, center);
            var limit = Math.Max(12, Math.Max(box.Width, track.Box.Width) * 0.9 * cfg.EdgeMatchScale);

            // 预测位置：趴桌时框会突然变矮、中心下移，靠速度预测能把它拉回原来的 ID
            var predicted = track.Predict(Math.Min(gap, 2.0));
            var predDist = Distance(predicted, center);
            var predLimit = Math.Max(12, box.Width * 1.1 * cfg.EdgeMatchScale);

            var sizeA = Math.Max(1, track.Box.Width * (double)track.Box.Height);
            var sizeB = Math.Max(1, box.Width * (double)box.Height);
            var sizeSim = Math.Min(sizeA, sizeB) / Math.Max(sizeA, sizeB);

            var prox = 1.0 - Math.Min(1.0, dist / limit);
            var predProx = 1.0 - Math.Min(1.0, predDist / predLimit);

            var score = 0.42 * iou
                        + 0.24 * prox
                        + 0.14 * sizeSim
                        + 0.20 * predProx;

            // 遮挡后重新出现：框位置接近就给一点补偿，否则 ID 会跳
            if (gap > cfg.MatchCacheSeconds) score += 0.05;

            if (score <= bestScore) continue;

            bestScore = score;
            bestIndexDistance = dist;
            best = i;
        }

        if (best < 0) return -1;
        if (bestScore >= 0.28) return best;

        // 分数不够但中心特别近（框尺寸剧烈变化时 IoU 会归零），也认
        return bestIndexDistance < Math.Max(10, box.Width * 0.45) ? best : -1;
    }

    /// <summary>把一帧观测推进到轨迹的状态机里。</summary>
    private static void Apply(
        PersonTrack track,
        Observation obs,
        DateTime now,
        DetectionSettings cfg,
        Rect previousBox,
        DateTime previousSeen,
        double delta,
        bool isNew)
    {
        track.JustConfirmed = false;
        track.JustWoke = false;

        track.HeadDown = obs.HeadDown;
        track.HeadJudgeable = obs.HeadJudgeable;
        track.PostureScore = obs.PostureScore;
        track.OcclusionScore = obs.OcclusionScore;
        track.HipsVisible = obs.HipsVisible;

        if (obs.EyesJudgeable)
        {
            track.LastEyeEvidence = now;
            track.EyesJudgeable = true;
            track.EyesOpen = obs.EyesOpen;

            if (obs.EyesOpen) track.LastEyeOpen = now;
        }
        else
        {
            track.EyesJudgeable = false;
            track.EyesOpen = false;
        }

        track.EyeOpenScore = obs.EyesJudgeable
            ? Math.Clamp(0.5 + (obs.EyeScore - cfg.EyeDarkRatio) * 2.0, 0, 1)
            : 0.5;

        // ---------- 1. 有效动作 ----------
        var moved = HasMoved(previousBox, obs.Box, obs, track);

        // 看到睁眼 = 清醒的直接证据，比任何姿态都硬
        if (obs.EyesJudgeable && obs.EyesOpen) moved = true;

        if (isNew)
        {
            track.LastMotion = now;
        }
        else if (moved)
        {
            track.LastMotion = now;
            track.MotionAccum += Distance(
                new Point2d(previousBox.X + previousBox.Width / 2.0, previousBox.Y + previousBox.Height / 2.0),
                new Point2d(obs.Box.X + obs.Box.Width / 2.0, obs.Box.Y + obs.Box.Height / 2.0));
        }
        else if (!(obs.HeadJudgeable || obs.EyesJudgeable))
        {
            // 判断不了的时候把计时器原地冻结，宁可少报不误报：
            // 把"上次动作时刻"跟着时间往前推，静置时长就不会继续涨
            track.LastMotion = track.LastMotion.AddSeconds(delta);
        }

        track.StillSeconds = Math.Max(0, (now - track.LastMotion).TotalSeconds);

        // ---------- 2. 判定阈值 ----------
        // 低头 + 没睁眼，判得更快；抬头坐着不动但闭眼，按完整时长算
        var need = Math.Max(2, cfg.SleepSeconds);
        var judgeable = obs.HeadJudgeable || obs.EyesJudgeable;
        var postureHint = obs.HeadDown && !obs.EyesOpen && judgeable;

        if (postureHint) need *= Math.Clamp(cfg.PostureWeight, 0.2, 1.0);

        track.RequiredSeconds = need;

        // 判定失效时不能下结论
        if (!judgeable)
        {
            track.State = TrackState.Unknown;
            track.DoubtSince = null;
            track.SleepConfidence = 0;
            return;
        }

        track.EverJudged = true;

        if (cfg.LegacyPostureMode)
        {
            // 经典模式：退回 1.4 的行为，闭眼 / 低头超过设定秒数直接算睡着
            var sleepByEye = (now - track.LastEyeEvidence).TotalSeconds <= 2.5 &&
                             (now - track.LastEyeOpen).TotalSeconds > need;
            var sleepByPosture = obs.HeadDown && track.StillSeconds > Math.Max(2, cfg.SleepSeconds);

            var sleeping = sleepByEye || sleepByPosture;
            var wasSleeping = track.State == TrackState.Sleeping;

            track.State = sleeping ? TrackState.Sleeping : TrackState.Awake;
            track.DoubtSince = sleeping ? track.DoubtSince ?? now : null;
            track.SleepConfidence = sleeping ? 0.9 : 0;

            if (sleeping && !wasSleeping)
            {
                track.JustConfirmed = true;
                track.SleepCount++;
            }

            if (!sleeping && wasSleeping) track.JustWoke = true;

            AccumulateTime(track, delta);
            return;
        }

        // ---------- 3. 四层状态机 ----------
        var previous = track.State;

        if (track.StillSeconds < need)
        {
            track.State = TrackState.Awake;
            track.DoubtSince = null;
        }
        else if (track.DoubtSince == null)
        {
            track.State = TrackState.Doubtful;
            track.DoubtSince = now;
        }
        else
        {
            var doubtSeconds = (now - track.DoubtSince.Value).TotalSeconds;

            // 疑似撑满准入时长才确认；中途有动作会走到上面的分支，直接退回清醒
            track.State = doubtSeconds >= cfg.ConfirmSeconds
                ? TrackState.Sleeping
                : TrackState.Doubtful;
        }

        if (track.State == TrackState.Sleeping && previous != TrackState.Sleeping)
        {
            track.JustConfirmed = true;
            track.SleepCount++;
        }

        if (track.State != TrackState.Sleeping && previous == TrackState.Sleeping)
        {
            track.JustWoke = true;
        }

        var stillRatio = Math.Min(1.0, track.StillSeconds / Math.Max(1.0, need + cfg.ConfirmSeconds));

        track.SleepConfidence = Math.Clamp(
            0.42 * track.PostureScore +
            0.22 * track.OcclusionScore +
            0.36 * stillRatio,
            0, 1);

        // 头部关键点留给下一帧做动作判定
        if (obs.HasHead)
        {
            track.PrevHeadX = obs.HeadX;
            track.PrevHeadY = obs.HeadY;
            track.HasPrevHead = true;
        }

        AccumulateTime(track, delta);
    }

    /// <summary>
    /// 有效动作判定。任何一个成立就重置静置计时：
    /// 人体框中心明显位移 / 头部关键点明显移动。
    /// 阈值跟着框宽走，远近距离用同一套判定也成立。
    /// </summary>
    private static bool HasMoved(Rect previousBox, Rect box, Observation obs, PersonTrack track)
    {
        if (track.HasPrevHead && obs.HasHead)
        {
            var hx = obs.HeadX - track.PrevHeadX;
            var hy = obs.HeadY - track.PrevHeadY;
            var headMove = Math.Sqrt(hx * hx + hy * hy);

            if (headMove > Math.Max(5, box.Width * 0.10)) return true;
        }

        var center = new Point2d(box.X + box.Width / 2.0, box.Y + box.Height / 2.0);
        var prev = new Point2d(previousBox.X + previousBox.Width / 2.0, previousBox.Y + previousBox.Height / 2.0);

        return Distance(center, prev) > Math.Max(6, previousBox.Width * 0.15);
    }

    /// <summary>
    /// 师生语义区分。全程不看脸、不看衣服，只用行为特征：
    /// <list type="bullet">
    /// <item>站姿 —— 髋部关键点可见且框高宽比很大；</item>
    /// <item>走动 —— 单位时间位移明显超过坐着的人；</item>
    /// <item>讲台区 —— 中心点落在画面上方那一带。</item>
    /// </list>
    /// 满足其中两条才算非学生，每 5 秒重判一次，避免被人站起来伸个懒腰就误判。
    /// </summary>
    private static void ClassifyRole(PersonTrack track, DateTime now, DetectionSettings cfg, int frameHeight)
    {
        if (!cfg.ExcludeTeacher || frameHeight <= 0) return;

        var elapsed = (now - track.LastRoleCheck).TotalSeconds;
        if (track.LastRoleCheck != DateTime.MinValue && elapsed < RoleCheckSeconds) return;

        track.LastRoleCheck = now;

        var aspect = track.Box.Height / (double)Math.Max(1, track.Box.Width);
        var standing = track.HipsVisible && aspect > 1.85;

        var speed = elapsed > 0.5 ? track.MotionAccum / elapsed : 0;
        var moving = elapsed > 0.5 && speed > track.Box.Width * 0.6;

        var centerY = track.Box.Y + track.Box.Height / 2.0;
        var inPodium = centerY < frameHeight * cfg.PodiumRatio;

        track.MotionAccum = 0;

        if (standing) track.StandingHits++;
        if (moving) track.MotionHits++;
        if (inPodium) track.RegionHits++;

        var hits = (standing ? 1 : 0) + (moving ? 1 : 0) + (inPodium ? 1 : 0);

        // 站起来 + 走动，是最典型的老师在讲台上踱步；
        // 只看"在讲台区"太容易把第一排学生算进去，所以必须凑够两条
        if (hits >= 2) track.IsTeacher = true;
        else if (hits == 0) track.IsTeacher = false;
    }

    private static void AccumulateTime(PersonTrack track, double delta)
    {
        if (delta <= 0 || delta > 5) return;

        if (track.State == TrackState.Sleeping) track.SleepingSeconds += delta;
        else if (track.State == TrackState.Awake) track.AwakeSeconds += delta;
    }

    private static double Distance(Point2d a, Point2d b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
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
