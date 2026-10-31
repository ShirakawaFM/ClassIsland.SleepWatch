using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ClassIsland.SleepWatch.Controls;

/// <summary>
/// 会流动的像素背景。
///
/// 做法不是画几万个矩形（那会把 UI 线程拖死），而是先把一张很小的位图
/// （大约 110×80）画好，再用最近邻插值放大铺满 —— 放大的过程天然产生硬边像素块，
/// 既好看又便宜。整块画面只有 8k 多个像素，一帧的活是纯整数加法。
///
/// 动效是自己的路子，没有抄任何现成皮肤：
/// <list type="bullet">
/// <item>三层不同速度的正弦场叠加，形成慢慢漂移的"热斑"，颜色只往主题色上偏；</item>
/// <item>十几颗亮点按固定节拍<b>整格跳动</b>（不是平滑滑动），像老式电平表在闪；</item>
/// <item>一道很淡的扫描带自上而下刷，给静止画面一点方向感。</item>
/// </list>
///
/// 时间基准是真实帧时间（<see cref="FrameDriver"/>），所以 60fps 和 30fps 下速度一致，
/// 掉帧只会少画几帧，不会让动画变慢或突然跳一下。
/// </summary>
public sealed class PixelFlowBackground : Control
{
    private WriteableBitmap? _bitmap;
    private int _width;
    private int _height;

    private FrameDriver? _driver;
    private readonly int[] _wave = new int[1024];

    private double _seconds;
    private Spark[] _sparks = Array.Empty<Spark>();

    private Color _background = Color.Parse("#F7F4EC");
    private Color _accent = Color.Parse("#D97757");
    private int _cell = 7;
    private bool _animated = true;

    /// <summary>亮点。位置是整格量化的，所以看起来是"跳"过去的。</summary>
    private struct Spark
    {
        public int X;
        public int Y;
        public int Dx;
        public int Dy;
        public int NextHopMs;
    }

    public PixelFlowBackground()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;

        // 缩放时不要做插值，否则像素味就没了
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);

        for (var i = 0; i < _wave.Length; i++)
        {
            _wave[i] = (int)(Math.Sin(i * Math.PI * 2 / _wave.Length) * 64);
        }

        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
    }

    // ---------------- 对外属性 ----------------

    public Color BackgroundColor
    {
        get => _background;
        set
        {
            if (_background == value) return;
            _background = value;
            Paint();
            InvalidateVisual();
        }
    }

    public Color AccentColor
    {
        get => _accent;
        set
        {
            if (_accent == value) return;
            _accent = value;
            Paint();
            InvalidateVisual();
        }
    }

    public int CellSize
    {
        get => _cell;
        set
        {
            _cell = Math.Clamp(value, 4, 18);

            var had = _bitmap != null;
            _bitmap?.Dispose();
            _bitmap = null;

            if (had) EnsureBitmap();
            InvalidateVisual();
        }
    }

    public bool Animated
    {
        get => _animated;
        set
        {
            if (_animated == value) return;
            _animated = value;

            if (value) Attach();
            else
            {
                _driver?.Stop();
                _driver = null;
            }

            Paint();
            InvalidateVisual();
        }
    }

    /// <summary>实际帧率，调试用。</summary>
    public double Fps { get; private set; }

    // ---------------- 帧驱动 ----------------

    private void Attach()
    {
        EnsureBitmap();

        if (!_animated) return;
        if (_driver != null) return;

        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        _driver = new FrameDriver(top, OnFrame);
        _driver.Start();
    }

    private void Detach()
    {
        _driver?.Dispose();
        _driver = null;
    }

    private void OnFrame(TimeSpan elapsed)
    {
        _seconds = elapsed.TotalSeconds;

        // 帧率滑窗估计，别让单帧抖动把数字打得乱跳
        var ms = _driver?.LastFrameMs ?? 16;
        if (ms > 0.1)
        {
            var inst = 1000.0 / ms;
            Fps = Fps <= 0 ? inst : Fps * 0.9 + inst * 0.1;
        }

        HopSparks(elapsed);
        Paint();
        InvalidateVisual();
    }

    /// <summary>亮点每 ~130ms 整格挪一次，制造"像素在跳"的手感，而不是平滑飘。</summary>
    private void HopSparks(TimeSpan elapsed)
    {
        var sparks = _sparks;
        var nowMs = (int)elapsed.TotalMilliseconds;

        for (var i = 0; i < sparks.Length; i++)
        {
            var s = sparks[i];
            if (nowMs < s.NextHopMs) continue;

            s.X += s.Dx;
            s.Y += s.Dy;

            if (s.X < 1 || s.X >= _width - 1) { s.Dx = -s.Dx; s.X = Math.Clamp(s.X, 1, Math.Max(1, _width - 2)); }
            if (s.Y < 1 || s.Y >= _height - 1) { s.Dy = -s.Dy; s.Y = Math.Clamp(s.Y, 1, Math.Max(1, _height - 2)); }

            s.NextHopMs = nowMs + 90 + (i * 17 % 130);
            sparks[i] = s;
        }
    }

    // ---------------- 尺寸与位图 ----------------

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        _bitmap?.Dispose();
        _bitmap = null;
        EnsureBitmap();
        InvalidateVisual();
    }

    private void EnsureBitmap()
    {
        if (_bitmap != null) return;

        var bounds = Bounds;
        if (bounds.Width < 2 || bounds.Height < 2) return;

        _width = Math.Clamp((int)Math.Ceiling(bounds.Width / Math.Max(4, _cell)), 16, 400);
        _height = Math.Clamp((int)Math.Ceiling(bounds.Height / Math.Max(4, _cell)), 16, 300);

        _bitmap = new WriteableBitmap(
            new PixelSize(_width, _height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        BuildSparks();
        Paint();
    }

    private void BuildSparks()
    {
        var random = new Random(20261031);
        var sparks = new Spark[14];

        for (var i = 0; i < sparks.Length; i++)
        {
            sparks[i] = new Spark
            {
                X = random.Next(1, Math.Max(2, _width - 1)),
                Y = random.Next(1, Math.Max(2, _height - 1)),
                Dx = random.Next(0, 2) == 0 ? -1 : 1,
                Dy = random.Next(0, 2) == 0 ? -1 : 1,
                NextHopMs = random.Next(0, 400)
            };
        }

        _sparks = sparks;
    }

    // ---------------- 绘制 ----------------

    private unsafe void Paint()
    {
        if (_bitmap == null) return;

        var bg = _background;
        var ac = _accent;

        // 底色里掺一点主题色，像素最低亮度也不是纯灰
        var baseR = (byte)(bg.R * 0.95 + ac.R * 0.05);
        var baseG = (byte)(bg.G * 0.95 + ac.G * 0.05);
        var baseB = (byte)(bg.B * 0.95 + ac.B * 0.05);

        var spanR = ac.R - baseR;
        var spanG = ac.G - baseG;
        var spanB = ac.B - baseB;

        var w = _width;
        var h = _height;

        // 三个方向上的相位，按秒走。速度刻意取互质的小数，画面很久才重复一次
        var t = _animated ? _seconds : 0;
        var t1 = (int)(t * 9);       // 水平漂移
        var t2 = (int)(t * 13);      // 竖直漂移
        var t3 = (int)(t * 21);      // 斜向

        // 扫描带：每 7 秒走一趟
        var bandY = (int)((t % 7.0) / 7.0 * (h + 40)) - 20;

        using var fb = _bitmap.Lock();

        for (var y = 0; y < h; y++)
        {
            var row = (uint*)((byte*)fb.Address + y * fb.RowBytes);

            var wy = (y * 11 - t2) & 1023;
            var checkerY = (y >> 1) & 1;

            var bandDist = Math.Abs(y - bandY);
            var bandBoost = bandDist < 10 ? (10 - bandDist) * 6 : 0;

            for (var x = 0; x < w; x++)
            {
                var wx = (x * 7 + t1) & 1023;
                var wd = ((x + y) * 5 + t3) & 1023;

                var v = (_wave[wx] + _wave[wy] + _wave[wd]) / 3;   // -64..64

                // 只有"高"的地方才吃主题色，低处保持干净，整体不会糊成一片
                var k = v > 0 ? v * v / 64 : 0;                    // 0..64
                if ((((x >> 1) ^ checkerY) & 1) == 1) k = k * 3 / 4;

                k = k * 2 + bandBoost;
                if (k > 205) k = 205;

                var r = (byte)(baseR + spanR * k / 255);
                var g = (byte)(baseG + spanG * k / 255);
                var b = (byte)(baseB + spanB * k / 255);

                row[x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
            }
        }

        // 亮点：一格实心 + 左边一格半透明，像拖着尾巴
        foreach (var s in _sparks)
        {
            if (s.X < 0 || s.X >= w || s.Y < 0 || s.Y >= h) continue;

            var row = (uint*)((byte*)fb.Address + s.Y * fb.RowBytes);
            row[s.X] = 0xFF000000u | ((uint)ac.R << 16) | ((uint)ac.G << 8) | ac.B;

            if (s.X > 0) row[s.X - 1] = Blend(row[s.X - 1], ac, 90);
            if (s.X + 1 < w) row[s.X + 1] = Blend(row[s.X + 1], ac, 60);
        }
    }

    private static uint Blend(uint original, Color accent, int alpha)
    {
        var r = (int)((original >> 16) & 0xFF);
        var g = (int)((original >> 8) & 0xFF);
        var b = (int)(original & 0xFF);

        r = (r * (255 - alpha) + accent.R * alpha) / 255;
        g = (g * (255 - alpha) + accent.G * alpha) / 255;
        b = (b * (255 - alpha) + accent.B * alpha) / 255;

        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);

        if (_bitmap == null || _width <= 0 || _height <= 0)
        {
            context.FillRectangle(new SolidColorBrush(_background), rect);
            return;
        }

        context.DrawImage(_bitmap, new Rect(0, 0, _width, _height), rect);
    }
}
