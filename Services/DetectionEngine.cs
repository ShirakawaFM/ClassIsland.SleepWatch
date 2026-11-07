using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using OpenCvSharp;

using SkiaSharp;

namespace ClassIsland.SleepWatch.Services;

public enum SleepLevel
{
    Idle,
    Good,
    Normal,
    Tired
}

/// <summary>一次结算的结果，界面只读这个对象。</summary>
public sealed class SleepSnapshot
{
    public int ClassSize { get; init; }

    /// <summary>画面里当前找到的学生。</summary>
    public int Present { get; init; }

    public int Awake { get; init; }

    /// <summary>确认沉睡人数（已过准入时长）。</summary>
    public int Sleeping { get; init; }

    /// <summary>疑似沉睡（静置计时中）人数。</summary>
    public int Doubtful { get; init; }

    /// <summary>看到了人但判断不了状态（转身、被挡、脸太小）。</summary>
    public int Unknown { get; init; }

    /// <summary>被判定为非学生（老师）而排除出统计的人数。</summary>
    public int Excluded { get; init; }

    /// <summary>本帧因为超出应到人数被丢弃的低置信度目标数。</summary>
    public int Filtered { get; init; }

    /// <summary>抬头率 0~1。</summary>
    public double HeadUpRate { get; init; }

    /// <summary>应到人数减去画面里找到的人。</summary>
    public int Unobserved { get; init; }

    public float Fps { get; init; }
    public SleepLevel Level { get; init; }
    public string Hint { get; init; } = "";
    public DateTime Timestamp { get; init; }

    /// <summary>麦克风被占用、正在降频跑。</summary>
    public bool Throttled { get; init; }

    public static readonly SleepSnapshot Empty = new()
    {
        Level = SleepLevel.Idle,
        Hint = "未启动"
    };
}

/// <summary>
/// 检测引擎。独立线程抓帧 → 姿态推理 → 状态机结算，UI 只轮询 Snapshot。
/// 不依赖任何 ClassIsland 服务，方便单独调试。
/// </summary>
public sealed class DetectionEngine : IDisposable
{
    private readonly object _gate = new();

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private VideoCapture? _capture;

    private PoseDetector? _pose;
    private readonly PersonTracker _tracker = new();
    private ChunkPlanner? _chunks;

    // 抓帧和缩放的 Mat 常驻复用：摄像头的分辨率不会变，每帧新开一块
    // 只是把同样的几 MB 内存搬来搬去，还给 GC 添活
    private Mat? _frame;
    private Mat? _small;

    private DetectionSettings _settings = new();
    private byte[]? _previewPng;
    private string _status = "未启动";
    private long _lastFrameTicks;
    private float _fps;

    /// <summary>1 = 正常，0.5 = 麦克风被占用，降一半帧率。</summary>
    private volatile float _fpsFactor = 1f;

    private bool _cleanupDone;
    private DateTime _cleanupDay = DateTime.MinValue;

    public DetectionSettings Settings => _settings;
    public SleepSnapshot Snapshot { get; private set; } = SleepSnapshot.Empty;
    public bool PreviewEnabled { get; set; }

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>当前是否在降频礼让（供设置页显示）。</summary>
    public bool Throttled => _fpsFactor < 0.99f;

    public string Status
    {
        get => _status;
        private set => _status = value;
    }

    /// <summary>UI 线程随时来取当前预览帧（PNG），没有就返回 null。</summary>
    public byte[]? TakePreview()
    {
        lock (_gate)
        {
            return _previewPng;
        }
    }

    // ---------- 生命周期 ----------

    public void Start(DetectionSettings settings)
    {
        Stop();

        _settings = settings.Clone();
        _settings.Normalize();
        AppLog.Configure(_settings.LogLevel);

        _pose = new PoseDetector();
        _tracker.Reset();
        _chunks = new ChunkPlanner(2, 2, 0.15);
        _fpsFactor = 1f;
        _cleanupDone = false;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // 麦克风礼让：别的程序占着麦克风就把帧率压一半，解除后自动恢复
        if (_settings.MicThrottle)
        {
            AudioDeviceMonitor.BusyChanged -= OnMicBusy;
            AudioDeviceMonitor.BusyChanged += OnMicBusy;
            AudioDeviceMonitor.Start();
            _fpsFactor = AudioDeviceMonitor.IsBusy ? 0.5f : 1f;
        }

        _thread = new Thread(() => Loop(token))
        {
            IsBackground = true,
            Name = "SleepWatch-Detection"
        };
        _thread.Start();

        AppLog.Info($"检测启动：{_settings.ProcessFps} FPS / 宽 {_settings.ProcessWidth} / " +
                    $"准入 {_settings.ConfirmSeconds:0}s / 分块 {(_settings.ChunkedInference ? "开" : "关")}");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            _thread?.Join(1500);
        }
        catch
        {
            // ignore
        }

        _thread = null;
        _cts?.Dispose();
        _cts = null;

        ReleaseCapture();

        _pose?.Dispose();
        _pose = null;

        _frame?.Dispose();
        _frame = null;

        _small?.Dispose();
        _small = null;

        AudioDeviceMonitor.BusyChanged -= OnMicBusy;
        AudioDeviceMonitor.Stop();
        _fpsFactor = 1f;

        lock (_gate)
        {
            _previewPng = null;
        }

        Snapshot = SleepSnapshot.Empty;
        Status = "已停止";
    }

    /// <summary>清空所有人的状态计时。</summary>
    public void Reset() => _tracker.Reset();

    private void OnMicBusy(bool busy)
    {
        _fpsFactor = busy ? 0.5f : 1f;

        // 恢复时稍等一下：别的程序刚放开麦克风，可能马上又有下一个来抢
        if (!busy)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(3000);
                if (!AudioDeviceMonitor.IsBusy) _fpsFactor = 1f;
            });
        }
    }

    // ---------- 主循环 ----------

    private void Loop(CancellationToken token)
    {
        Status = "正在加载模型…";

        // 先把原生 OpenCV 库的搜索路径接上，否则插件加载上下文里找不到 OpenCvSharpExtern.dll
        NativeBootstrap.Ensure();

        var posePath = ModelManager.Resolve(ModelManager.PoseFile);

        if (posePath == null)
        {
            Status = "缺少姿态模型，已在这些位置找过：\n" + ModelManager.DescribeSearch();
            Snapshot = new SleepSnapshot
            {
                Level = SleepLevel.Idle,
                Hint = "模型未就绪",
                ClassSize = _settings.ClassSize,
                Timestamp = DateTime.Now
            };
            return;
        }

        if (_pose == null || !_pose.Load(posePath))
        {
            Status = _pose?.LastError ?? "姿态模型初始化失败";
            AppLog.Error("姿态模型初始化失败：" + Status);
            return;
        }

        Status = "正在连接视频源…";

        while (!token.IsCancellationRequested)
        {
            var fps = Math.Max(1, _settings.ProcessFps) * _fpsFactor;
            var interval = Math.Max(33, (int)(1000 / Math.Max(1, fps)));
            var sw = Stopwatch.StartNew();

            try
            {
                if (!EnsureCapture())
                {
                    Thread.Sleep(1000);
                    continue;
                }

                _frame ??= new Mat();
                if (_capture == null || !_capture.Read(_frame) || _frame.Empty())
                {
                    Status = "读取画面失败，重连中…";
                    ReleaseCapture();
                    Thread.Sleep(800);
                    continue;
                }

                ProcessFrame(_frame, token);
            }
            catch (Exception ex)
            {
                Status = "运行异常：" + ex.Message;
                AppLog.Error("检测循环异常", ex);
                ReleaseCapture();
                Thread.Sleep(800);
            }

            var wait = interval - (int)sw.ElapsedMilliseconds;
            if (wait > 0)
            {
                try
                {
                    Thread.Sleep(wait);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private bool EnsureCapture()
    {
        if (_capture is { } c && c.IsOpened()) return true;

        ReleaseCapture();

        var source = _settings.VideoSource?.Trim() ?? "0";

        try
        {
            if (int.TryParse(source, out var index))
            {
                var cap = new VideoCapture();
                if (!cap.Open(index, VideoCaptureAPIs.DSHOW))
                {
                    cap.Open(index, VideoCaptureAPIs.MSMF);
                }

                if (!cap.IsOpened())
                {
                    cap.Open(index, VideoCaptureAPIs.ANY);
                }

                if (cap.IsOpened())
                {
                    cap.Set(VideoCaptureProperties.BufferSize, 1);
                }

                _capture = cap;
            }
            else
            {
                var cap = new VideoCapture(source, VideoCaptureAPIs.FFMPEG);
                if (cap.IsOpened())
                {
                    cap.Set(VideoCaptureProperties.BufferSize, 1);
                }

                _capture = cap;
            }
        }
        catch (Exception ex)
        {
            Status = "打开视频源失败：" + ex.Message;
            AppLog.Error("打开视频源失败", ex);
            return false;
        }

        if (_capture == null || !_capture.IsOpened())
        {
            _capture?.Dispose();
            _capture = null;
            Status = $"打不开视频源（{source}），请检查摄像头编号";
            return false;
        }

        Status = "监测中";
        AppLog.Info("视频源已连接：" + source);
        return true;
    }

    private void ReleaseCapture()
    {
        try
        {
            _capture?.Release();
        }
        catch
        {
            // ignore
        }

        try
        {
            _capture?.Dispose();
        }
        catch
        {
            // ignore
        }

        _capture = null;
    }

    // ---------- 单帧处理 ----------

    private void ProcessFrame(Mat frame, CancellationToken token)
    {
        if (_pose == null) return;

        var targetW = Math.Min(_settings.ProcessWidth, frame.Width);
        if (targetW <= 0) return;

        var scale = targetW / (double)frame.Width;
        var targetH = Math.Max(1, (int)(frame.Height * scale));

        _small ??= new Mat();
        Cv2.Resize(frame, _small, new Size(targetW, targetH), 0, 0, InterpolationFlags.Area);
        var small = _small;

        var now = DateTime.Now;

        var options = new PoseOptions
        {
            MinScore = _settings.MinPoseScore,
            EyeThreshold = _settings.EyeDarkRatio,
            HeadDownThreshold = _settings.HeadDownThreshold,
            SingleEyeMode = _settings.SingleEyeMode,
            TrustPostureOverEyes = _settings.TrustPostureOverEyes
        };

        List<PosePerson> persons;

        if (_settings.ChunkedInference && _chunks != null && _chunks.Count > 1)
        {
            var region = _chunks.Next(small.Width, small.Height, out var chunkIndex);

            persons = _pose.Detect(small, new PoseOptions
            {
                MinScore = options.MinScore,
                EyeThreshold = options.EyeThreshold,
                HeadDownThreshold = options.HeadDownThreshold,
                SingleEyeMode = options.SingleEyeMode,
                TrustPostureOverEyes = options.TrustPostureOverEyes,
                Region = region
            });

            _chunks.Report(chunkIndex, persons.Count);
        }
        else
        {
            persons = _pose.Detect(small, options);
        }

        var report = _tracker.Update(persons, now, _settings, small.Width, small.Height);

        // 静默记账：只写"确认沉睡"，疑似和判断不了的都不入库
        try
        {
            SleepLedger.Instance.Observe(report, now, _settings.ClassSize);
        }
        catch (Exception ex)
        {
            // 记账出错不能停掉检测
            AppLog.Error("记账失败", ex);
        }

        CaptureSnapshots(small, report, now);

        Snapshot = BuildSnapshot(report, now);

        // 帧率统计
        var ticks = now.Ticks;
        if (_lastFrameTicks > 0)
        {
            var dt = (ticks - _lastFrameTicks) / (double)TimeSpan.TicksPerSecond;
            if (dt > 0.0001)
            {
                var instant = (float)(1.0 / dt);
                _fps = _fps <= 0 ? instant : _fps * 0.8f + instant * 0.2f;
            }
        }

        _lastFrameTicks = ticks;

        if (PreviewEnabled && !token.IsCancellationRequested)
        {
            BuildPreview(small, report);
        }

        // 快照清理：每天第一次跑到这里时做一次，别在循环里翻目录
        if (!_cleanupDone || _cleanupDay != now.Date)
        {
            _cleanupDone = true;
            _cleanupDay = now.Date;

            if (_settings.SnapshotEnabled)
            {
                ThreadPool.QueueUserWorkItem(_ => SnapshotService.Cleanup(_settings));
            }
        }
    }

    /// <summary>确认沉睡的那一下抓一张局部图。同一段睡眠只抓一次。</summary>
    private void CaptureSnapshots(Mat small, TrackingReport report, DateTime now)
    {
        if (!_settings.SnapshotEnabled || small.Empty()) return;

        var lesson = SleepLedger.Instance.Current;

        foreach (var track in report.Tracks)
        {
            if (!track.JustConfirmed || track.IsTeacher) continue;

            var start = track.DoubtSince ?? now;
            var path = SnapshotService.Capture(
                small,
                track.Box,
                lesson,
                track.Id,
                start,
                null,
                track.SleepConfidence,
                _settings);

            if (path.Length == 0) continue;

            SleepLedger.Instance.AttachSnapshot(track.Id, start, path);
            AppLog.Debug($"抓拍：轨迹 #{track.Id}，置信 {track.SleepConfidence:0.00}");
        }
    }

    private SleepSnapshot BuildSnapshot(TrackingReport report, DateTime now)
    {
        var unobserved = Math.Max(0, _settings.ClassSize - report.Present);

        // 等级看的是"确认沉睡 + 半个疑似"：疑似还没坐实，不该把整班算成倒下一片
        var weighted = report.Sleeping + report.Doubtful * 0.5;
        var ratio = report.Present > 0 ? weighted / report.Present : 0;

        SleepLevel level;
        string hint;

        if (report.Present <= 0)
        {
            level = SleepLevel.Idle;
            hint = "画面里没找到人";
        }
        else if (weighted <= 0)
        {
            level = SleepLevel.Good;
            hint = "没人趴下";
        }
        else if (ratio <= 0.10 && weighted <= 2)
        {
            level = SleepLevel.Good;
            hint = "状态不错";
        }
        else if (ratio <= 0.25)
        {
            level = SleepLevel.Normal;
            hint = "有点乏了";
        }
        else
        {
            level = SleepLevel.Tired;
            hint = "倒下一片";
        }

        if (report.Filtered > 0) hint += $" · 已过滤 {report.Filtered} 个低置信度目标";

        if (report.Present > 0 && _fps > 0)
        {
            hint += $" · {_fps:0.0} FPS";
        }

        if (Throttled) hint += " · 降频中";

        return new SleepSnapshot
        {
            ClassSize = _settings.ClassSize,
            Present = report.Present,
            Awake = report.Awake,
            Sleeping = report.Sleeping,
            Doubtful = report.Doubtful,
            Unknown = report.Unknown,
            Excluded = report.Excluded,
            Filtered = report.Filtered,
            HeadUpRate = report.HeadUpRate,
            Unobserved = unobserved,
            Fps = _fps,
            Level = level,
            Hint = hint,
            Timestamp = now,
            Throttled = Throttled
        };
    }

    // ---------- 预览 ----------

    private static readonly SKColor ColorAwake = new(120, 220, 120);
    private static readonly SKColor ColorSleep = new(224, 80, 80);
    private static readonly SKColor ColorUnknown = new(200, 200, 90);
    private static readonly SKColor ColorDoubt = new(240, 160, 48);
    private static readonly SKColor ColorTeacher = new(90, 156, 240);

    /// <summary>
    /// 画标注框 + 状态标签 + 底部统计。
    /// 用 SkiaSharp 而不是 Cv2.PutText，因为 OpenCV 自带字体只有 ASCII，写不了「疑似」这些字。
    /// </summary>
    private void BuildPreview(Mat small, TrackingReport report)
    {
        try
        {
            using var src = SKImage.FromPixelCopy(
                new SKImageInfo(small.Width, small.Height, SKColorType.Bgra8888, SKAlphaType.Opaque),
                small.Data,
                (int)small.Step());

            if (src == null) return;

            const int footer = 22;

            using var surface = SKSurface.Create(
                new SKImageInfo(small.Width, small.Height + footer, SKColorType.Bgra8888, SKAlphaType.Premul));

            if (surface == null) return;

            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(16, 16, 18));
            canvas.DrawImage(src, new SKRect(0, 0, small.Width, small.Height));

            using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei")
                                 ?? SKTypeface.FromFamilyName("SimHei")
                                 ?? SKTypeface.Default;

            using var font = new SKFont(typeface, 11);

            foreach (var track in report.Tracks)
            {
                var color = track.IsTeacher ? ColorTeacher : track.State switch
                {
                    TrackState.Sleeping => ColorSleep,
                    TrackState.Doubtful => ColorDoubt,
                    TrackState.Awake => ColorAwake,
                    _ => ColorUnknown
                };

                using var stroke = new SKPaint
                {
                    Color = color,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2
                };

                var rect = new SKRect(track.Box.X, track.Box.Y, track.Box.Right, track.Box.Bottom);
                canvas.DrawRect(rect, stroke);

                var label = track.IsTeacher ? "老师" : track.State switch
                {
                    TrackState.Sleeping => "沉睡",
                    TrackState.Doubtful => $"疑似 {track.StillSeconds:0}s",
                    TrackState.Awake => "清醒",
                    _ => "判不清"
                };

                DrawTag(canvas, font, rect, $"#{track.Id} {label}", color);

                // 疑似：框底画一条进度，直观看到离确认还有多久
                if (track.State == TrackState.Doubtful && track.RequiredSeconds > 0)
                {
                    var total = Math.Max(1, track.RequiredSeconds + _settings.ConfirmSeconds);
                    var done = Math.Min(1.0, (track.StillSeconds) / total);

                    using var bar = new SKPaint { Color = color };
                    canvas.DrawRect(
                        new SKRect(rect.Left, rect.Bottom - 5, rect.Left + (float)(rect.Width * done), rect.Bottom),
                        bar);
                }
            }

            DrawFooter(canvas, font, small.Width, small.Height, footer, report);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            if (data == null) return;

            lock (_gate)
            {
                _previewPng = data.ToArray();
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("预览绘制失败：" + ex.Message);
        }
    }

    private static void DrawTag(SKCanvas canvas, SKFont font, SKRect box, string text, SKColor color)
    {
        var width = font.MeasureText(text, out var bounds);

        var x = box.Left;
        var y = box.Top - 4 >= bounds.Height ? box.Top - 4 : box.Top + bounds.Height + 4;

        using var background = new SKPaint { Color = new SKColor(0, 0, 0, 150) };
        canvas.DrawRect(new SKRect(x - 2, y - bounds.Height, x + width + 4, y + 3), background);

        using var fill = new SKPaint { Color = color };
        canvas.DrawText(text, x, y - 2, font, fill);
    }

    private static void DrawFooter(SKCanvas canvas, SKFont font, int width, int height, int footer, TrackingReport report)
    {
        using var background = new SKPaint { Color = new SKColor(24, 24, 28) };
        canvas.DrawRect(new SKRect(0, height, width, height + footer), background);

        using var text = new SKPaint { Color = new SKColor(210, 210, 215) };

        var line = $"在座 {report.Present}   清醒 {report.Awake}   沉睡 {report.Sleeping}   " +
                   $"疑似 {report.Doubtful}   判不清 {report.Unknown}   排除 {report.Excluded}   " +
                   $"抬头 {report.HeadUpRate * 100:0}%";

        canvas.DrawText(line, 8, height + 15, font, text);
    }

    public void Dispose() => Stop();
}
