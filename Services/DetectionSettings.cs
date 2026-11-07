using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 检测参数。对应配置文件 settings.json，全部可以在设置页里改。
///
/// 兼容约定：新增字段一律带默认值，旧配置反序列化后缺的项自动落回默认，
/// 所以从 1.4 升上来不用重新配。字段名不要改——改了等于把用户的配置清零。
/// </summary>
public class DetectionSettings
{
    // ---------------- 视频源 ----------------

    /// <summary>视频源。填 "0"/"1" 走本机摄像头索引，填 rtsp:// 或 http:// 走网络流。</summary>
    public string VideoSource { get; set; } = "0";

    /// <summary>每秒处理几帧。4 帧足够判断"几秒没睁眼"，再高只是白烧 CPU。</summary>
    public int ProcessFps { get; set; } = 4;

    /// <summary>送去推理的帧宽度。640 在一体机上大约占 30% 单核。</summary>
    public int ProcessWidth { get; set; } = 640;

    /// <summary>应到人数。既用来算"画面里没找到的人"，也当检测人数的硬上限（0 = 不限制）。</summary>
    public int ClassSize { get; set; } = 45;

    // ---------------- 判定 ----------------

    /// <summary>
    /// 基础静置判定时长（秒）。目标连续多久没有有效动作，就从「清醒」转入「疑似沉睡」。
    /// 这是核心参数：姿势不再是主判据，动没动才是。
    /// </summary>
    public double SleepSeconds { get; set; } = 8;

    /// <summary>
    /// 沉睡记录准入时长（秒）。疑似状态再持续这么久，才跃迁为「确认沉睡」并写入正式记录。
    /// 瞬时低头、捡笔、挠头这类小动作一般撑不过这个时长，不会入库、不会抓拍。
    /// </summary>
    public double ConfirmSeconds { get; set; } = 30;

    /// <summary>姿态加权系数。低头 + 没睁眼时，静置判定时长乘这个系数，判得更快。</summary>
    public double PostureWeight { get; set; } = 0.6;

    /// <summary>经典姿态判定模式：回退到 1.4 的行为（低头/闭眼直接算睡着），给不习惯新规则的老师留个开关。</summary>
    public bool LegacyPostureMode { get; set; }

    /// <summary>多久没在画面里出现，就不再算这个人"在座"。</summary>
    public double PresenceMinutes { get; set; } = 6;

    /// <summary>睁眼判定阈值。眼部暗像素占比高于这个值算睁眼。</summary>
    public double EyeDarkRatio { get; set; } = 0.10;

    /// <summary>人体检测置信度下限。教室人多、目标小，不能设太高。</summary>
    public float MinPoseScore { get; set; } = 0.32f;

    /// <summary>低头判定阈值。头部相对肩线的落差（除以肩宽）超过它就算低头。</summary>
    public float HeadDownThreshold { get; set; } = -0.12f;

    // ---------------- 跟踪 ----------------

    /// <summary>
    /// 遮挡续踪时长（秒）。人整个被挡住、框也检测不到时，轨迹保留多久。
    /// 趴桌把脸埋进胳膊里是最常见的情况，留短了 ID 就会跳。
    /// </summary>
    public double OcclusionSeconds { get; set; } = 12;

    /// <summary>短暂消失（漏检、侧身）的待匹配缓存时长，比完全遮挡短一些。</summary>
    public double MatchCacheSeconds { get; set; } = 6;

    /// <summary>自动排除老师等站立人员。</summary>
    public bool ExcludeTeacher { get; set; } = true;

    /// <summary>讲台区域高度比例：画面上方这一段算讲台，站在里面的人优先判成老师。</summary>
    public double PodiumRatio { get; set; } = 0.2;

    /// <summary>侧脸时只有一只眼可判，也算可判。</summary>
    public bool SingleEyeMode { get; set; } = true;

    /// <summary>边缘目标匹配放宽倍数。</summary>
    public float EdgeMatchScale { get; set; } = 1.0f;

    /// <summary>后排目标少信眼睛、多信姿态。</summary>
    public bool TrustPostureOverEyes { get; set; } = true;

    // ---------------- 推理 ----------------

    /// <summary>分块推理：每帧只算一个块，四帧覆盖全画面，单帧开销砍掉一大半。</summary>
    public bool ChunkedInference { get; set; } = true;

    /// <summary>机位预设序号，见 <see cref="DetectionPresets"/>。</summary>
    public int Preset { get; set; }

    // ---------------- 生命周期 ----------------

    /// <summary>上课时自动开始检测。</summary>
    public bool AutoStartOnClass { get; set; } = true;

    /// <summary>下课时自动停止检测，并且把摄像头句柄一起释放。</summary>
    public bool AutoStopOnBreak { get; set; } = true;

    /// <summary>
    /// 只在这些科目的课上自动开检测（按科目名）。空列表 = 全选，跟着 <see cref="AutoStartOnClass"/> 走。
    /// 手动点【重启监测】不受这个限制。
    /// </summary>
    public List<string> EnabledSubjects { get; set; } = new();

    // ---------------- 资源礼让 ----------------

    /// <summary>其他程序占用麦克风时自动降频。</summary>
    public bool MicThrottle { get; set; } = true;

    // ---------------- 证据快照 ----------------

    /// <summary>确认沉睡时抓一张局部快照。默认关：很多人不希望存图。</summary>
    public bool SnapshotEnabled { get; set; }

    /// <summary>快照保留天数。</summary>
    public int SnapshotKeepDays { get; set; } = 120;

    /// <summary>快照存放目录。留空就用配置目录下的 snapshots。</summary>
    public string SnapshotFolder { get; set; } = "";

    /// <summary>JPEG 质量。</summary>
    public int SnapshotQuality { get; set; } = 70;

    // ---------------- 诊断 ----------------

    /// <summary>日志级别：Error / Warn / Info / Debug。</summary>
    public string LogLevel { get; set; } = "Info";

    public DetectionSettings Clone()
    {
        var copy = (DetectionSettings)MemberwiseClone();
        copy.EnabledSubjects = new List<string>(EnabledSubjects ?? new List<string>());
        return copy;
    }

    public void Normalize()
    {
        if (ProcessFps < 1) ProcessFps = 1;
        if (ProcessFps > 30) ProcessFps = 30;
        if (ClassSize < 0) ClassSize = 0;
        if (ClassSize > 200) ClassSize = 200;
        if (SleepSeconds < 2) SleepSeconds = 2;
        if (SleepSeconds > 300) SleepSeconds = 300;
        if (ConfirmSeconds < 5) ConfirmSeconds = 5;
        if (ConfirmSeconds > 120) ConfirmSeconds = 120;
        if (PostureWeight < 0.2) PostureWeight = 0.2;
        if (PostureWeight > 1.0) PostureWeight = 1.0;
        if (PresenceMinutes < 1) PresenceMinutes = 1;
        if (PresenceMinutes > 120) PresenceMinutes = 120;
        if (EyeDarkRatio < 0.01) EyeDarkRatio = 0.01;
        if (EyeDarkRatio > 0.60) EyeDarkRatio = 0.60;
        if (ProcessWidth < 320) ProcessWidth = 320;
        if (ProcessWidth > 1920) ProcessWidth = 1920;
        if (MinPoseScore < 0.10f) MinPoseScore = 0.10f;
        if (MinPoseScore > 0.90f) MinPoseScore = 0.90f;
        if (HeadDownThreshold < -0.60f) HeadDownThreshold = -0.60f;
        if (HeadDownThreshold > 0.20f) HeadDownThreshold = 0.20f;
        if (OcclusionSeconds < 2) OcclusionSeconds = 2;
        if (OcclusionSeconds > 60) OcclusionSeconds = 60;
        if (MatchCacheSeconds < 1) MatchCacheSeconds = 1;
        if (MatchCacheSeconds > OcclusionSeconds) MatchCacheSeconds = OcclusionSeconds;
        if (PodiumRatio < 0.05) PodiumRatio = 0.05;
        if (PodiumRatio > 0.5) PodiumRatio = 0.5;
        if (EdgeMatchScale < 0.5f) EdgeMatchScale = 0.5f;
        if (EdgeMatchScale > 2.5f) EdgeMatchScale = 2.5f;
        if (Preset < 0) Preset = 0;
        if (Preset >= DetectionPresets.All.Count) Preset = 0;
        if (SnapshotKeepDays < 7) SnapshotKeepDays = 7;
        if (SnapshotKeepDays > 365) SnapshotKeepDays = 365;
        if (SnapshotQuality < 40) SnapshotQuality = 40;
        if (SnapshotQuality > 95) SnapshotQuality = 95;
        if (string.IsNullOrWhiteSpace(VideoSource)) VideoSource = "0";

        EnabledSubjects ??= new List<string>();
        SnapshotFolder ??= "";
        LogLevel = string.IsNullOrWhiteSpace(LogLevel) ? "Info" : LogLevel.Trim();
    }

    /// <summary>这门课要不要自动开检测。科目为空或者列表为空时一律放行。</summary>
    public bool SubjectAllowed(string? subject)
    {
        if (EnabledSubjects == null || EnabledSubjects.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(subject)) return true;

        return EnabledSubjects.Any(s => string.Equals(s, subject, StringComparison.Ordinal));
    }
}

/// <summary>
/// 配置读写。放在插件配置目录，和 ClassIsland 的档案互不影响。
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static string FilePath => Path.Combine(Plugin.ConfigFolder, "settings.json");

    public static DetectionSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<DetectionSettings>(json, Options);
                if (s != null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("配置文件读取失败，改用默认值：" + ex.Message);
        }

        var def = new DetectionSettings();
        def.Normalize();
        return def;
    }

    public static void Save(DetectionSettings settings)
    {
        try
        {
            settings.Normalize();
            Directory.CreateDirectory(Plugin.ConfigFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex)
        {
            AppLog.Error("配置文件写入失败", ex);
        }
    }
}
