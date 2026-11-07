using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

using OpenCvSharp;

namespace ClassIsland.SleepWatch.Services;

/// <summary>打开失败的大致原因，用来给用户一句人话。</summary>
public enum CameraFailReason
{
    None,

    /// <summary>这个序号上没有设备。</summary>
    NotFound,

    /// <summary>有这个设备，但打不开 —— 被别的程序独占，或者驱动没就绪。</summary>
    Busy,

    /// <summary>OpenCvSharp 的原生库没加载起来，跟摄像头本身无关。</summary>
    NativeMissing,

    /// <summary>其它异常。</summary>
    Error
}

public sealed class CameraOpenResult
{
    /// <summary>成功时返回已经预热好、可以立刻读帧的实例；失败时为 null。</summary>
    public VideoCapture? Capture { get; init; }

    public CameraFailReason Reason { get; init; }

    /// <summary>给日志和界面用的一句话。</summary>
    public string Message { get; init; } = "";

    /// <summary>最后成功/失败在哪个后端上。</summary>
    public VideoCaptureAPIs Api { get; init; }

    public bool Ok => Capture != null;

    public static CameraOpenResult Fail(CameraFailReason reason, string message, VideoCaptureAPIs api)
        => new() { Reason = reason, Message = message, Api = api };
}

/// <summary>
/// 摄像头打开的唯一入口。
///
/// 原来枚举和引擎各写了一套打开逻辑，两边的容错程度还不一样，于是出现
/// 「设置页说没找到摄像头，但引擎其实能用」这种自相矛盾的情况。统一到这里之后，
/// 两个地方对同一个设备给出同样的结论。
///
/// 踩过的坑：
/// - <c>Open</c> 返回 true 不等于能出画面。摄像头刚上电、分辨率切换中，前几帧经常是空的，
///   必须预热到拿到非空帧才算真的打开，否则会被误判成"设备不可用"。
/// - OpenCvSharp 4.9 **没有** <c>OpenTimeoutMsec</c>，<c>Open</c> 也没有带属性的重载，
///   所以打开动作没有任何超时保护，只能在调用方放到后台线程里跑。
/// - 默认走 DSHOW：实测比 MSMF 慢（这台机器 600ms vs 350ms），但兼容性最好，
///   虚拟摄像头和采集卡在 MSMF 上翻车的概率明显更高。
/// </summary>
public static class CameraOpener
{
    /// <summary>按可靠性排序的后端。DSHOW 优先，MSMF 兜底，ANY 最后。</summary>
    private static readonly VideoCaptureAPIs[] Backends =
    {
        VideoCaptureAPIs.DSHOW,
        VideoCaptureAPIs.MSMF,
        VideoCaptureAPIs.ANY
    };

    /// <summary>枚举时最多试到几号。再大就是无意义的等待。</summary>
    public const int MaxIndex = 6;

    /// <summary>打开之后最多花多久等第一帧。</summary>
    private const int WarmupBudgetMs = 1500;

    /// <summary>
    /// 按 OpenCV 序号打开本机摄像头。
    /// </summary>
    /// <param name="index">设备序号，从 0 开始。</param>
    public static CameraOpenResult OpenIndex(int index)
    {
        NativeBootstrap.Ensure();

        Exception? fatal = null;
        var sawException = false;

        foreach (var api in Backends)
        {
            if (!TryOne(index, api, out var capture, out var ex))
            {
                if (ex != null)
                {
                    sawException = true;
                    // 原生库问题不分后端，第一次撞上就没必要再试别的
                    if (IsNativeFailure(ex))
                    {
                        AppLog.Error($"摄像头 {index} 打开失败（{api}）：原生库没加载起来", ex);
                        return CameraOpenResult.Fail(CameraFailReason.NativeMissing,
                            "OpenCvSharp 的原生库没加载起来，摄像头调不了", api);
                    }

                    fatal ??= ex;
                }

                continue;
            }

            // 打开成功还要能出画面，否则等于没打开
            if (!Warmup(capture))
            {
                AppLog.Warn($"摄像头 {index}（{api}）打开了但读不到画面，继续换后端");
                SafeRelease(ref capture);
                continue;
            }

            AppLog.Info($"摄像头 {index} 已打开（{api}，{capture.FrameWidth}x{capture.FrameHeight}）");
            return new CameraOpenResult
            {
                Capture = capture,
                Reason = CameraFailReason.None,
                Message = $"已连接摄像头 {index}",
                Api = api
            };
        }

        // 全都打不开：区分"没这个设备"和"有但用不了"
        // 后端不是在 0 号上试的（试了 3 次都失败），说明序号本身大概率越界
        var hint = fatal?.Message;
        if (sawException && fatal != null && !IsNativeFailure(fatal))
        {
            AppLog.Warn($"摄像头 {index} 三次都打不开：{hint}");
        }

        // DSHOW 在序号不存在时是"秒失败"，而"有设备但被独占"通常会挣扎一会儿；
        // 这里只看 ANY 是否需要更多时间来判断，简单可靠
        return CameraOpenResult.Fail(
            CameraFailReason.NotFound,
            string.IsNullOrWhiteSpace(hint) ? $"摄像头 {index} 打不开" : $"摄像头 {index} 打不开：{hint}",
            VideoCaptureAPIs.ANY);
    }

    /// <summary>打开 rtsp:// / http:// 之类的网络流。</summary>
    public static CameraOpenResult OpenUrl(string url)
    {
        NativeBootstrap.Ensure();

        VideoCapture? capture = null;
        try
        {
            capture = new VideoCapture(url, VideoCaptureAPIs.FFMPEG);
            if (!capture.IsOpened())
            {
                SafeRelease(ref capture);
                return CameraOpenResult.Fail(CameraFailReason.NotFound,
                    $"连不上网络视频源 {Shorten(url)}", VideoCaptureAPIs.FFMPEG);
            }

            if (!Warmup(capture))
            {
                SafeRelease(ref capture);
                return CameraOpenResult.Fail(CameraFailReason.Busy,
                    $"网络视频源 {Shorten(url)} 连上了但读不到画面", VideoCaptureAPIs.FFMPEG);
            }

            AppLog.Info($"网络视频源已连接：{Shorten(url)}");
            return new CameraOpenResult
            {
                Capture = capture,
                Reason = CameraFailReason.None,
                Message = "已连接网络视频源",
                Api = VideoCaptureAPIs.FFMPEG
            };
        }
        catch (Exception ex)
        {
            SafeRelease(ref capture);
            AppLog.Error($"打开网络视频源失败：{Shorten(url)}", ex);
            return CameraOpenResult.Fail(
                IsNativeFailure(ex) ? CameraFailReason.NativeMissing : CameraFailReason.Error,
                ex.Message, VideoCaptureAPIs.FFMPEG);
        }
    }

    /// <summary>这个序号上能不能真的出画面。枚举用它判断设备可用性。</summary>
    public static bool Probe(int index) => OpenIndex(index).Ok;

    private static bool TryOne(int index, VideoCaptureAPIs api,
        [NotNullWhen(true)] out VideoCapture? capture, out Exception? error)
    {
        capture = null;
        error = null;

        try
        {
            // 每次都新建：同一个实例连续 Open 不同后端，OpenCV 内部状态可能没清干净
            var cap = new VideoCapture();
            if (!cap.Open(index, api) || !cap.IsOpened())
            {
                SafeRelease(ref cap);
                return false;
            }

            // DSHOW 不支持 BufferSize（Set 会返回 false），失败就算了，别当成错误
            cap.Set(VideoCaptureProperties.BufferSize, 1);

            capture = cap;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>
    /// 等第一帧。给一段时间反复读，别把"还没准备好"当成"设备坏了"。
    /// </summary>
    private static bool Warmup(VideoCapture capture)
    {
        var sw = Stopwatch.StartNew();

        using var frame = new Mat();

        while (sw.ElapsedMilliseconds < WarmupBudgetMs)
        {
            try
            {
                if (capture.Read(frame) && !frame.Empty()) return true;
            }
            catch
            {
                return false;
            }

            Thread.Sleep(40);
        }

        return false;
    }

    private static void SafeRelease(ref VideoCapture? capture)
    {
        try
        {
            capture?.Release();
        }
        catch
        {
            // ignore
        }

        try
        {
            capture?.Dispose();
        }
        catch
        {
            // ignore
        }

        capture = null;
    }

    /// <summary>顺着 InnerException 找原生库加载失败的痕迹。</summary>
    private static bool IsNativeFailure(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return true;
            }
        }

        return false;
    }

    private static string Shorten(string url)
        => url.Length <= 48 ? url : url[..45] + "…";
}
