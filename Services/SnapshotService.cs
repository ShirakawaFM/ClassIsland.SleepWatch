using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using OpenCvSharp;

using SkiaSharp;

namespace ClassIsland.SleepWatch.Services;

/// <summary>快照路径换算。存的是相对路径，换台机器、换个盘符都不会失效。</summary>
public static class SnapshotPaths
{
    /// <summary>默认根目录：配置目录下的 snapshots。</summary>
    public static string DefaultRoot => Path.Combine(Plugin.ConfigFolder, "snapshots");

    /// <summary>用户配的目录；配错了或者建不出来就退回默认目录。</summary>
    public static string Root(DetectionSettings? settings = null)
    {
        var configured = settings?.SnapshotFolder?.Trim();

        if (string.IsNullOrWhiteSpace(configured)) return DefaultRoot;

        try
        {
            Directory.CreateDirectory(configured);

            // 真写一个探针文件，确认这个目录不是只读挂载或者没有权限
            var probe = Path.Combine(configured, ".write-test");
            File.WriteAllText(probe, "x");
            File.Delete(probe);

            return configured;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"快照目录不可用（{configured}），改用默认目录：{ex.Message}");
            return DefaultRoot;
        }
    }

    public static string Absolute(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return "";

        try
        {
            return Path.IsPathRooted(relative) ? relative : Path.Combine(Root(), relative);
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>
/// 证据快照。
///
/// 只在「疑似 → 确认沉睡」的那一下抓一张，同一段睡眠只抓一次。
/// 存的不是整幅课堂画面，而是该目标人体框外扩 20% 的局部裁剪 ——
/// 既能看清"确实趴着了"，又不会把别人一起存进去。
///
/// 标注写在图片顶部的一条黑边上：课程、入睡到醒来的时间、匿名轨迹 ID、置信度。
/// 中文用 SkiaSharp 画（OpenCV 自带的 Hershey 字体只有 ASCII，写不了中文）。
/// </summary>
public static class SnapshotService
{
    private static readonly object Gate = new();

    /// <summary>外扩比例：把人和桌面一起框进去，否则只看一个后脑勺没什么说服力。</summary>
    private const double Margin = 0.2;

    /// <summary>裁剪图放大到这个宽度再存，缩略图才看得清。</summary>
    private const int MinWidth = 320;

    /// <summary>
    /// 抓一张。返回相对路径，失败返回空串（绝不抛）。
    /// </summary>
    public static string Capture(
        Mat frame,
        Rect box,
        LessonRecord? lesson,
        int trackId,
        DateTime sleepStart,
        DateTime? sleepEnd,
        double confidence,
        DetectionSettings settings)
    {
        if (frame.Empty() || box.Width < 8 || box.Height < 8) return "";

        try
        {
            var root = SnapshotPaths.Root(settings);
            var day = sleepStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var lessonPart = SafeName(lesson?.Id ?? "unsorted");

            var dir = Path.Combine(root, day, lessonPart);
            Directory.CreateDirectory(dir);

            var name = $"{trackId}_{sleepStart:yyyyMMddHHmmss}.jpg";
            var full = Path.Combine(dir, name);

            if (File.Exists(full)) return Path.Combine(day, lessonPart, name).Replace('\\', '/');

            using var cropped = Crop(frame, box);
            if (cropped.Empty()) return "";

            WriteJpeg(cropped, full, lesson, trackId, sleepStart, sleepEnd, confidence, settings);

            return Path.Combine(day, lessonPart, name).Replace('\\', '/');
        }
        catch (Exception ex)
        {
            AppLog.Warn("证据快照写入失败：" + ex.Message);
            return "";
        }
    }

    /// <summary>人体框外扩 20%，并夹在画面内。</summary>
    private static Mat Crop(Mat frame, Rect box)
    {
        var padX = (int)(box.Width * Margin);
        var padY = (int)(box.Height * Margin);

        var x = Math.Max(0, box.X - padX);
        var y = Math.Max(0, box.Y - padY);
        var w = Math.Min(frame.Width - x, box.Width + padX * 2);
        var h = Math.Min(frame.Height - y, box.Height + padY * 2);

        if (w < 8 || h < 8) return new Mat();

        var roi = new Mat(frame, new Rect(x, y, w, h));
        var copy = roi.Clone();
        roi.Dispose();

        return copy;
    }

    private static void WriteJpeg(
        Mat crop,
        string path,
        LessonRecord? lesson,
        int trackId,
        DateTime start,
        DateTime? end,
        double confidence,
        DetectionSettings settings)
    {
        // 缩放比例：太小的裁剪图放大到 320 宽，标注才写得下
        var scale = Math.Max(1.0, MinWidth / (double)crop.Width);
        var width = (int)(crop.Width * scale);
        var height = (int)(crop.Height * scale);

        const int headerHeight = 46;

        // 同样是 BGR → BGRA，少这一步截图会一张都写不出来
        using var bgra = PixelBridge.ToBgra(crop);
        using var src = PixelBridge.ToImage(bgra);

        if (src == null) return;

        using var surface = SKSurface.Create(new SKImageInfo(width, height + headerHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface == null) return;

        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(18, 18, 20));

        // 先把裁剪图铺上去。
        // 这里刻意用不带 SKPaint 的重载：SKPaint.FilterQuality 是 2.88 的写法，
        // SkiaSharp 3.x 已经删了；插件要在正式版（2.88）和预览版（3.x）都能跑，就别碰它。
        canvas.DrawImage(src, new SKRect(0, headerHeight, width, headerHeight + height));

        DrawHeader(canvas, width, headerHeight, lesson, trackId, start, end, confidence);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(settings.SnapshotQuality, 40, 95));
        if (data == null) return;

        using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);
    }

    private static void DrawHeader(
        SKCanvas canvas,
        int width,
        int height,
        LessonRecord? lesson,
        int trackId,
        DateTime start,
        DateTime? end,
        double confidence)
    {
        using var background = new SKPaint { Color = new SKColor(24, 24, 28) };
        canvas.DrawRect(new SKRect(0, 0, width, height), background);

        using var accent = new SKPaint { Color = new SKColor(217, 119, 87) };
        canvas.DrawRect(new SKRect(0, 0, 3, height), accent);

        using var typeface = ResolveTypeface();

        var title = lesson == null || string.IsNullOrWhiteSpace(lesson.Title)
            ? "未分类课程"
            : lesson.Title;

        var span = end == null
            ? $"{start:HH:mm:ss} – 未结束"
            : $"{start:HH:mm:ss} – {end.Value:HH:mm:ss}";

        using var titleFont = new SKFont(typeface, 13);
        using var subFont = new SKFont(typeface, 11.5f);

        using var white = new SKPaint { Color = new SKColor(245, 245, 247) };
        using var dim = new SKPaint { Color = new SKColor(190, 190, 196) };

        canvas.DrawText(title, 12, 18, titleFont, white);
        canvas.DrawText($"#{trackId}  {span}  置信 {confidence:0.00}", 12, 36, subFont, dim);
    }

    /// <summary>找一个能写中文的字体。找不到就退回默认字体（会显示方框，但不会崩）。</summary>
    private static SKTypeface ResolveTypeface()
    {
        foreach (var name in new[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "SimSun" })
        {
            var face = SKTypeface.FromFamilyName(name);
            if (face != null) return face;
        }

        return SKTypeface.Default;
    }

    private static string SafeName(string value)
    {
        var chars = value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    /// <summary>
    /// 按保留天数清旧文件。只在启动和每天第一次抓拍时跑，别在检测循环里翻目录。
    /// </summary>
    public static void Cleanup(DetectionSettings settings)
    {
        lock (Gate)
        {
            try
            {
                var root = SnapshotPaths.Root(settings);
                if (!Directory.Exists(root)) return;

                var cutoff = DateTime.Today.AddDays(-Math.Max(7, settings.SnapshotKeepDays));
                var removed = 0;

                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(dir);

                    if (!DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var date))
                    {
                        continue;
                    }

                    if (date >= cutoff) continue;

                    Directory.Delete(dir, recursive: true);
                    removed++;
                }

                if (removed > 0) AppLog.Info($"已清理 {removed} 个过期快照目录（保留 {settings.SnapshotKeepDays} 天）");
            }
            catch (Exception ex)
            {
                AppLog.Warn("清理过期快照失败：" + ex.Message);
            }
        }
    }

    /// <summary>统计快照占用，设置页显示用。</summary>
    public static (int Files, long Bytes) Usage(DetectionSettings settings)
    {
        try
        {
            var root = SnapshotPaths.Root(settings);
            if (!Directory.Exists(root)) return (0, 0);

            var files = Directory.GetFiles(root, "*.jpg", SearchOption.AllDirectories);
            return (files.Length, files.Sum(f => new FileInfo(f).Length));
        }
        catch
        {
            return (0, 0);
        }
    }
}
