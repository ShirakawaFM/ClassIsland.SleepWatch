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
/// 做法不是画几万个矩形（那会把 UI 线程拖死），而是先渲染一张很小的位图
/// （大约 150×100），再用最近邻插值放大铺满窗口 —— 放大的过程天然产生硬边像素块，
/// 既好看又便宜。每帧只算整数加法和表查找，没有三角函数。
/// </summary>
public sealed class PixelFlowBackground : Control
{
    private WriteableBitmap? _bitmap;
    private int _width;
    private int _height;

    private readonly DispatcherTimer _timer;
    private readonly int[] _wave = new int[1024];

    private int _tick;
    private Spark[] _sparks = Array.Empty<Spark>();

    private Color _background = Color.Parse("#F7F4EC");
    private Color _accent = Color.Parse("#D97757");
    private int _cell = 7;
    private bool _animated = true;

    private readonly struct Spark
    {
        public float X { get; init; }
        public float Y { get; init; }
        public float Vx { get; init; }
        public float Vy { get; init; }
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

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(72) };
        _timer.Tick += (_, _) => Step();

        AttachedToVisualTree += (_, _) =>
        {
            EnsureBitmap();
            if (_animated) _timer.Start();
            InvalidateVisual();
        };

        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    // ---------------- 对外属性 ----------------

    public Color BackgroundColor
    {
        get => _background;
        set
        {
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
            _bitmap?.Dispose();
            _bitmap = null;
            EnsureBitmap();
            InvalidateVisual();
        }
    }

    public bool Animated
    {
        get => _animated;
        set
        {
            _animated = value;

            if (value) _timer.Start();
            else _timer.Stop();

            Paint();
            InvalidateVisual();
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
        // 几颗慢慢飘的亮点，让静态帧也有方向感
        var random = new Random(7);
        var sparks = new Spark[22];

        for (var i = 0; i < sparks.Length; i++)
        {
            sparks[i] = new Spark
            {
                X = (float)(random.NextDouble() * _width),
                Y = (float)(random.NextDouble() * _height),
                Vx = (float)((random.NextDouble() - 0.5) * 0.16),
                Vy = (float)((random.NextDouble() - 0.5) * 0.10)
            };
        }

        _sparks = sparks;
    }

    // ---------------- 动画 ----------------

    private void Step()
    {
        _tick++;

        // 亮点飘动，越界就从另一边回来
        var sparks = _sparks;

        for (var i = 0; i < sparks.Length; i++)
        {
            var s = sparks[i];
            var x = s.X + s.Vx;
            var y = s.Y + s.Vy;

            if (x < 0) x += _width;
            if (x >= _width) x -= _width;
            if (y < 0) y += _height;
            if (y >= _height) y -= _height;

            sparks[i] = new Spark { X = x, Y = y, Vx = s.Vx, Vy = s.Vy };
        }

        Paint();
        InvalidateVisual();
    }

    private unsafe void Paint()
    {
        if (_bitmap == null) return;

        var bg = _background;
        var ac = _accent;

        // 背景色里掺一点主题色，作为像素最暗的底色
        var baseR = (byte)(bg.R * 0.94 + ac.R * 0.06);
        var baseG = (byte)(bg.G * 0.94 + ac.G * 0.06);
        var baseB = (byte)(bg.B * 0.94 + ac.B * 0.06);

        var t = _animated ? _tick : 0;

        using var fb = _bitmap.Lock();

        unsafe
        {
            for (var y = 0; y < _height; y++)
            {
                var row = (uint*)((byte*)fb.Address + y * fb.RowBytes);

                var wy = y * 5 - t * 2;
                var checkerY = (y >> 1) & 1;

                // 一道慢慢向下走的扫描带，像老显示器刷新的那一条
                var band = ((t * 2) + y) % (_height + 34);
                var bandBoost = band < 14 ? (14 - band) * 7 : 0;

                for (var x = 0; x < _width; x++)
                {
                    var wx = x * 3 + t * 3;
                    var wd = (x + y) * 2 + t * 4;

                    var v = (_wave[wx & 1023] + _wave[wy & 1023] + _wave[wd & 1023]) / 3; // -64..64

                    // 只让"高"的地方沾主题色，低的地方保持干净
                    var k = v > 0 ? v * v / 64 : 0; // 0..64
                    if ((((x >> 1) ^ checkerY) & 1) == 1) k = k * 3 / 4;

                    k = k * 3 / 2 + bandBoost; // 整体提一点，扫描带上更强
                    if (k > 200) k = 200;

                    var r = baseR + (ac.R - baseR) * k / 255;
                    var g = baseG + (ac.G - baseG) * k / 255;
                    var b = baseB + (ac.B - baseB) * k / 255;

                    row[x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
                }
            }

            // 亮点
            foreach (var s in _sparks)
            {
                var sx = (int)s.X;
                var sy = (int)s.Y;

                if (sx < 0 || sx >= _width || sy < 0 || sy >= _height) continue;

                var row = (uint*)((byte*)fb.Address + sy * fb.RowBytes);
                row[sx] = 0xFF000000u | ((uint)ac.R << 16) | ((uint)ac.G << 8) | ac.B;

                if (sx > 0)
                {
                    var left = (uint*)((byte*)fb.Address + sy * fb.RowBytes);
                    left[sx - 1] = Blend(left[sx - 1], ac, 90);
                }
            }
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
