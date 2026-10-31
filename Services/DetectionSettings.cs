using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 检测参数。对应配置文件 settings.json，全部可以在设置页里改。
/// 没有需要用肉眼调的标定项，装好就能跑。
/// </summary>
public class DetectionSettings
{
    /// <summary>视频源。填 "0"/"1" 走本机摄像头索引，填 rtsp:// 或 http:// 走网络流。</summary>
    public string VideoSource { get; set; } = "0";

    /// <summary>每秒处理几帧。4 帧足够判断"几秒没睁眼"，再高只是白烧 CPU。</summary>
    public int ProcessFps { get; set; } = 4;

    /// <summary>送去推理的帧宽度。640 在一体机上大约占 30% 单核。</summary>
    public int ProcessWidth { get; set; } = 640;

    /// <summary>应到人数。只用来算"画面里没找到的人"，填个大概就行。</summary>
    public int ClassSize { get; set; } = 45;

    /// <summary>核心参数：连续多少秒没抓到睁眼 / 一直低着头，就判定疑似睡着。</summary>
    public double SleepSeconds { get; set; } = 8;

    /// <summary>多久没在画面里出现，就不再算这个人"在座"。</summary>
    public double PresenceMinutes { get; set; } = 6;

    /// <summary>睁眼判定阈值。眼部暗像素占比高于这个值算睁眼。</summary>
    public double EyeDarkRatio { get; set; } = 0.10;

    /// <summary>人体检测置信度下限。教室人多、目标小，不能设太高。</summary>
    public float MinPoseScore { get; set; } = 0.35f;

    /// <summary>上课时自动开始检测。</summary>
    public bool AutoStartOnClass { get; set; } = true;

    /// <summary>下课时自动停止检测。</summary>
    public bool AutoStopOnBreak { get; set; } = true;

    public DetectionSettings Clone() => (DetectionSettings)MemberwiseClone();

    public void Normalize()
    {
        if (ProcessFps < 1) ProcessFps = 1;
        if (ProcessFps > 30) ProcessFps = 30;
        if (ClassSize < 1) ClassSize = 1;
        if (SleepSeconds < 2) SleepSeconds = 2;
        if (SleepSeconds > 300) SleepSeconds = 300;
        if (PresenceMinutes < 1) PresenceMinutes = 1;
        if (EyeDarkRatio < 0.01) EyeDarkRatio = 0.01;
        if (EyeDarkRatio > 0.60) EyeDarkRatio = 0.60;
        if (ProcessWidth < 320) ProcessWidth = 320;
        if (ProcessWidth > 1920) ProcessWidth = 1920;
        if (MinPoseScore < 0.10f) MinPoseScore = 0.10f;
        if (MinPoseScore > 0.90f) MinPoseScore = 0.90f;
        if (string.IsNullOrWhiteSpace(VideoSource)) VideoSource = "0";
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
        catch
        {
            // 配置损坏就用默认值，不要让插件起不来
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
        catch
        {
            // 写盘失败静默处理，避免在 UI 线程抛异常
        }
    }
}
