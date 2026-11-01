using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using OpenCvSharp;

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

    /// <summary>画面里当前找到的人。</summary>
    public int Present { get; init; }

    public int Awake { get; init; }
    public int Sleeping { get; init; }

    /// <summary>看到了人但判断不了状态（转身、被挡、脸太小）。</summary>
    public int Unknown { get; init; }

    /// <summary>应到人数减去画面里找到的人。</summary>
    public int Unobserved { get; init; }

    public float Fps { get; init; }
    public SleepLevel Level { get; init; }
    public string Hint { get; init; } = "";
    public DateTime Timestamp { get; init; }

    public static readonly SleepSnapshot Empty = new()
    {
        Level = SleepLevel.Idle,
        Hint = "未启动"
    };
}

/// <summary>
/// 检测引擎。独立线程抓帧 → 姿态 + 人脸 → 状态机结算，UI 只轮询 Snapshot。
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

    // 抓帧和缩放的 Mat 常驻复用：摄像头的分辨率不会变，每帧新开一块
    // 只是把同样的几 MB 内存搬来搬去，还给 GC 添活
    private Mat? _frame;
    private Mat? _small;

    private DetectionSettings _settings = new();
    private byte[]? _previewPng;
    private string _status = "未启动";
    private long _lastFrameTicks;
    private float _fps;

    public DetectionSettings Settings => _settings;
    public SleepSnapshot Snapshot { get; private set; } = SleepSnapshot.Empty;
    public bool PreviewEnabled { get; set; }

    public bool IsRunning => _thread is { IsAlive: true };

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

        _pose = new PoseDetector();
        _tracker.Reset();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _thread = new Thread(() => Loop(token))
        {
            IsBackground = true,
            Name = "SleepWatch-Detection"
        };
        _thread.Start();
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

        lock (_gate)
        {
            _previewPng = null;
        }

        Snapshot = SleepSnapshot.Empty;
        Status = "已停止";
    }

    /// <summary>清空所有人的状态计时。</summary>
    public void Reset() => _tracker.Reset();

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
            return;
        }

        Status = "正在连接视频源…";

        while (!token.IsCancellationRequested)
        {
            var interval = Math.Max(33, 1000 / Math.Max(1, _settings.ProcessFps));
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

        var persons = _pose.Detect(small, _settings.MinPoseScore, _settings.EyeDarkRatio);

        var report = _tracker.Update(persons, now, _settings);

        // 静默记账：把"谁睡着了、谁醒了"喂给账本，课后总结全靠它
        try
        {
            SleepLedger.Instance.Observe(report, now, _settings.ClassSize);
        }
        catch
        {
            // 记账出错不能停掉检测
        }

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
    }

    private SleepSnapshot BuildSnapshot(TrackingReport report, DateTime now)
    {
        var unobserved = Math.Max(0, _settings.ClassSize - report.Present);
        var ratio = report.Present > 0 ? report.Sleeping / (double)report.Present : 0;

        SleepLevel level;
        string hint;

        if (report.Present <= 0)
        {
            level = SleepLevel.Idle;
            hint = "画面里没找到人";
        }
        else if (report.Sleeping <= 0)
        {
            level = SleepLevel.Good;
            hint = "没人趴下";
        }
        else if (ratio <= 0.10 && report.Sleeping <= 2)
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

        if (report.Present > 0 && _fps > 0)
        {
            hint += $" · {_fps:0.0} FPS";
        }

        return new SleepSnapshot
        {
            ClassSize = _settings.ClassSize,
            Present = report.Present,
            Awake = report.Awake,
            Sleeping = report.Sleeping,
            Unknown = report.Unknown,
            Unobserved = unobserved,
            Fps = _fps,
            Level = level,
            Hint = hint,
            Timestamp = now
        };
    }

    private static readonly Scalar ColorAwake = new(120, 220, 120);
    private static readonly Scalar ColorSleep = new(80, 80, 240);
    private static readonly Scalar ColorUnknown = new(180, 180, 90);

    private void BuildPreview(Mat small, TrackingReport report)
    {
        try
        {
            using var canvas = small.Clone();

            foreach (var track in report.Tracks)
            {
                var color = track.State switch
                {
                    TrackState.Awake => ColorAwake,
                    TrackState.Sleeping => ColorSleep,
                    _ => ColorUnknown
                };

                Cv2.Rectangle(canvas, track.Box, color, 2);

                var tag = track.State switch
                {
                    TrackState.Sleeping => track.HeadDown ? "SLEEP(down)" : "SLEEP(eye)",
                    TrackState.Awake => track.HeadDown ? "awake/down" : "awake",
                    _ => "?"
                };

                Cv2.PutText(
                    canvas,
                    $"#{track.Id} {tag}",
                    new Point(track.Box.X, Math.Max(12, track.Box.Y - 5)),
                    HersheyFonts.HersheySimplex,
                    0.45,
                    color,
                    1);
            }

            Cv2.ImEncode(".png", canvas, out var png);

            lock (_gate)
            {
                _previewPng = png;
            }
        }
        catch
        {
            // 预览失败不影响主流程
        }
    }

    public void Dispose() => Stop();
}
