using OpenCvSharp;

using SkiaSharp;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// OpenCV 的 Mat 交给 SkiaSharp 之前的格式桥。
///
/// 摄像头帧是 <b>3 通道 BGR</b>，而 <see cref="SKColorType.Bgra8888"/> 是每像素 4 字节。
/// 直接把 BGR 的裸内存塞进去，<c>SKImage.FromPixelCopy</c> 会因为「一行字节数不够」
/// 返回 null —— 不抛异常、不打日志，调用方一句 <c>if (src == null) return;</c>
/// 就把整条渲染链路静默掐断了。
///
/// 1.5.0 把预览从 <c>Cv2.ImEncode</c> 换成 Skia 画中文标签时踩的就是这个：
/// 实时画面永远停在「正在等第一帧…」，快照文件也一张都写不出来。
/// </summary>
internal static class PixelBridge
{
    /// <summary>
    /// 转成连续的 4 通道 BGRA 副本。返回的 Mat 由调用方负责 Dispose。
    /// </summary>
    public static Mat ToBgra(Mat source)
    {
        var bgra = new Mat();

        switch (source.Channels())
        {
            case 3:
                Cv2.CvtColor(source, bgra, ColorConversionCodes.BGR2BGRA);
                break;

            case 4:
                // 少数采集卡直接给 BGRA，原样搬一份，顺便保证内存连续
                source.CopyTo(bgra);
                break;

            case 1:
                Cv2.CvtColor(source, bgra, ColorConversionCodes.GRAY2BGRA);
                break;

            default:
                bgra.Dispose();
                throw new NotSupportedException($"不支持的通道数：{source.Channels()}");
        }

        return bgra;
    }

    /// <summary>
    /// 把已经转好的 BGRA Mat 包成 SKImage。尺寸对不上时返回 null。
    /// </summary>
    public static SKImage? ToImage(Mat bgra)
    {
        if (bgra.Empty() || bgra.Type() != MatType.CV_8UC4) return null;

        return SKImage.FromPixelCopy(
            new SKImageInfo(bgra.Width, bgra.Height, SKColorType.Bgra8888, SKAlphaType.Opaque),
            bgra.Data,
            (int)bgra.Step());
    }
}
