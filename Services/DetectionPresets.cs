using System.Collections.Generic;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 机位预设。教室的机位差别很大：吊在天花板后方俯拍、讲台正对着拍、或者斜着架在角上。
/// 三种机位看到的肩线角度、眼睛像素数、侧脸程度都不一样，同一套阈值不可能都合适。
///
/// 预设只调「判定倾向」，不换模型、不改分辨率，所以切换是瞬时的，
/// 用户套完还可以继续手动微调。
/// </summary>
public sealed class DetectionPreset
{
    public string Name { get; init; } = "";

    /// <summary>人体检测置信度下限。后排远距离要更宽容，前排可以更严。</summary>
    public float MinPoseScore { get; init; } = 0.35f;

    /// <summary>低头判定的落差阈值。斜角机位下肩线被压扁，需要放宽。</summary>
    public float HeadDownThreshold { get; init; } = -0.12f;

    /// <summary>姿态加权系数：低头且没睁眼时，静置时长打几折。</summary>
    public double PostureWeight { get; init; } = 0.6;

    /// <summary>侧脸时只有一只眼可判，也算数。</summary>
    public bool SingleEyeOk { get; init; } = true;

    /// <summary>边缘/侧身目标的匹配宽容度（IoU 门槛的缩放）。</summary>
    public float EdgeMatchScale { get; init; } = 1.0f;

    /// <summary>后排目标降低眼部权重：眼睛只有几个像素时，宁可多靠姿态和静置。</summary>
    public bool TrustPostureOverEyes { get; init; }

    public string Note { get; init; } = "";
}

public static class DetectionPresets
{
    public static IReadOnlyList<DetectionPreset> All { get; } = new List<DetectionPreset>
    {
        new()
        {
            Name = "后方俯拍",
            MinPoseScore = 0.32f,
            HeadDownThreshold = -0.12f,
            PostureWeight = 0.60,
            SingleEyeOk = true,
            EdgeMatchScale = 1.0f,
            TrustPostureOverEyes = true,
            Note = "从教室后上方往下看，后排小、前排大。默认按这个机位调。"
        },
        new()
        {
            Name = "讲台正拍",
            MinPoseScore = 0.36f,
            HeadDownThreshold = -0.10f,
            PostureWeight = 0.65,
            SingleEyeOk = true,
            EdgeMatchScale = 1.0f,
            TrustPostureOverEyes = false,
            Note = "摄像头在讲台上正对学生，脸部看得最清楚，眼睛信号可以多信一点。"
        },
        new()
        {
            Name = "侧面斜拍",
            MinPoseScore = 0.30f,
            HeadDownThreshold = -0.18f,
            PostureWeight = 0.55,
            SingleEyeOk = true,
            EdgeMatchScale = 1.35f,
            TrustPostureOverEyes = true,
            Note = "斜架在教室一角。侧脸、侧身多，肩线被压扁，所以低头阈值和匹配都放宽。"
        }
    };

    public static DetectionPreset Get(int index)
        => index >= 0 && index < All.Count ? All[index] : All[0];

    /// <summary>把预设写进配置。用户之后手动改的项不会被覆盖，除非再套一次预设。</summary>
    public static void ApplyTo(DetectionSettings settings, int index)
    {
        var preset = Get(index);

        settings.Preset = index;
        settings.MinPoseScore = preset.MinPoseScore;
        settings.HeadDownThreshold = preset.HeadDownThreshold;
        settings.PostureWeight = preset.PostureWeight;
        settings.SingleEyeMode = preset.SingleEyeOk;
        settings.EdgeMatchScale = preset.EdgeMatchScale;
        settings.TrustPostureOverEyes = preset.TrustPostureOverEyes;

        settings.Normalize();
    }
}
