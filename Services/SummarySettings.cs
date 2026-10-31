using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using Avalonia.Media;

namespace ClassIsland.SleepWatch.Services;

/// <summary>一套配色。界面里所有颜色都从这里来，不散落在各个控件上。</summary>
public sealed class Palette
{
    public string Name { get; init; } = "";
    public string Background { get; init; } = "#F7F4EC";
    public string Surface { get; init; } = "#FFFCF6";
    public string Accent { get; init; } = "#D97757";
    public string Text { get; init; } = "#2E2A26";

    public static IReadOnlyList<Palette> Presets { get; } = new List<Palette>
    {
        new() { Name = "暖橘", Background = "#F7F4EC", Surface = "#FFFCF6", Accent = "#D97757", Text = "#2E2A26" },
        new() { Name = "午夜", Background = "#14161C", Surface = "#1C2029", Accent = "#7FB3FF", Text = "#E8ECF4" },
        new() { Name = "苔绿", Background = "#EDF2E6", Surface = "#F8FBF1", Accent = "#6E8B5A", Text = "#2C3327" },
        new() { Name = "海盐", Background = "#EAF2F4", Surface = "#F7FCFD", Accent = "#3E8E9C", Text = "#222E31" },
        new() { Name = "樱花", Background = "#FBF0F2", Surface = "#FFF8FA", Accent = "#D9678A", Text = "#3A2B2F" },
        new() { Name = "墨白", Background = "#F1F1EF", Surface = "#FCFCFB", Accent = "#4B4B48", Text = "#1E1E1C" }
    };
}

/// <summary>
/// 课后总结的配置。独立于检测参数，改配色、改显示项都不影响检测。
/// </summary>
public class SummarySettings
{
    // ---------- 行为 ----------
    /// <summary>静默检测：课堂上什么都不弹，只把睡眠事件记下来。</summary>
    public bool SilentMode { get; set; }

    /// <summary>下课自动弹出总结窗口。</summary>
    public bool AutoPopupOnLessonEnd { get; set; } = true;

    /// <summary>窗口里自动逐日轮播。</summary>
    public bool AutoPlay { get; set; } = true;

    /// <summary>放完末页自动关窗。默认开：看完就散，别留一个窗口在那。</summary>
    public bool AutoCloseAtEnd { get; set; } = true;

    /// <summary>每页停几秒。</summary>
    public int SlideSeconds { get; set; } = 7;

    /// <summary>总共回看几天。</summary>
    public int HistoryDays { get; set; } = 7;

    // ---------- 显示哪些块 ----------
    public bool ShowTotalSleepers { get; set; } = true;
    public bool ShowCurve { get; set; } = true;
    public bool ShowRepeat { get; set; } = true;
    public bool ShowAwakened { get; set; } = true;
    public bool ShowLongest { get; set; } = true;
    public bool ShowFacts { get; set; } = true;
    public bool ShowPeak { get; set; } = true;

    // ---------- 外观 ----------
    public string Background { get; set; } = "#F7F4EC";
    public string Surface { get; set; } = "#FFFCF6";
    public string Accent { get; set; } = "#D97757";
    public string Text { get; set; } = "#2E2A26";

    /// <summary>背景的像素流动动画。关掉就是一整块纯色，省电。</summary>
    public bool PixelAnimation { get; set; } = true;

    /// <summary>像素块大小，越小越细。</summary>
    public int PixelSize { get; set; } = 7;

    public SummarySettings Clone() => (SummarySettings)MemberwiseClone();

    public void Normalize()
    {
        if (SlideSeconds < 3) SlideSeconds = 3;
        if (SlideSeconds > 60) SlideSeconds = 60;
        if (HistoryDays < 1) HistoryDays = 1;
        if (HistoryDays > 30) HistoryDays = 30;
        if (PixelSize < 4) PixelSize = 4;
        if (PixelSize > 18) PixelSize = 18;

        Background = SafeColor(Background, "#F7F4EC");
        Surface = SafeColor(Surface, "#FFFCF6");
        Accent = SafeColor(Accent, "#D97757");
        Text = SafeColor(Text, "#2E2A26");
    }

    /// <summary>非法颜色直接换回默认值，避免界面里出现透明块。</summary>
    private static string SafeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            _ = Color.Parse(value);
            return value;
        }
        catch
        {
            return fallback;
        }
    }
}

public static class SummarySettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(Plugin.ConfigFolder, "summary.json");

    public static SummarySettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<SummarySettings>(File.ReadAllText(FilePath), Options);
                if (s != null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch
        {
            // 配置坏了就用默认
        }

        var def = new SummarySettings();
        def.Normalize();
        return def;
    }

    public static void Save(SummarySettings settings)
    {
        try
        {
            settings.Normalize();
            Directory.CreateDirectory(Plugin.ConfigFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // 静默失败，不能让 UI 线程崩
        }
    }
}
