using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using ClassIsland.SleepWatch.Services;

namespace ClassIsland.SleepWatch.Controls;

/// <summary>
/// 人数变化曲线。故意用柱状像素条而不是平滑曲线 —— 跟整页的像素风格统一，
/// 而且 20 秒一个采样点本来就是离散的，画成平滑曲线反而在撒谎。
///
/// 动画不归它自己管：外面的帧驱动器每帧调 <see cref="SetProgress"/>，
/// 柱子按「从左到右依次弹出」的节奏从零长到目标高度，折线跟着柱子顶端走。
/// 这样整页（数字、卡片、曲线）共用一条时间轴，不会各跑各的。
///
/// 文字用 <see cref="FormattedText"/> 缓存，只在高亮/换页时重建 ——
/// 每帧 new 十来个 FormattedText 是 60fps 下最容易被忽略的开销。
/// </summary>
public sealed class SleepCurveChart : Control
{
    private IReadOnlyList<CurvePoint> _points = Array.Empty<CurvePoint>();
    private string _startLabel = "";
    private string _endLabel = "";

    private Color _accent = Color.Parse("#D97757");
    private Color _text = Color.Parse("#2E2A26");
    private Color _background = Color.Parse("#FFFCF6");

    private double _progress = 1;

    private FormattedText? _startText;
    private FormattedText? _endText;
    private FormattedText? _emptyText;
    private FormattedText[] _axisText = Array.Empty<FormattedText>();

    // 每帧都要用的画笔也缓存，避免每帧 new SolidColorBrush
    private IBrush _faint = Brushes.Transparent;
    private IBrush _label = Brushes.Gray;
    private IBrush _accentBrush = Brushes.Orange;
    private IBrush[] _barBody = Array.Empty<IBrush>();
    private Pen? _linePen;
    private Pen? _peakPen;

    // 柱子顶端和"长完了没有"每帧重算，数组换成复用的，省掉入场那 0.7 秒里的 GC
    private Point[] _tops = Array.Empty<Point>();
    private bool[] _grown = Array.Empty<bool>();

    private int _max = 1;
    private int _steps;

    public double Progress => _progress;

    /// <summary>0~1。外面的帧驱动器每帧调它。</summary>
    public void SetProgress(double value)
    {
        var v = value <= 0 ? 0 : value >= 1 ? 1 : value;
        if (Math.Abs(v - _progress) < 0.0005) return;

        _progress = v;
        InvalidateVisual();
    }

    public void SetData(
        IReadOnlyList<CurvePoint> points,
        string startLabel,
        string endLabel,
        Color accent,
        Color text,
        Color background,
        bool animate = true)
    {
        _points = points ?? Array.Empty<CurvePoint>();
        _startLabel = startLabel ?? "";
        _endLabel = endLabel ?? "";
        _accent = accent;
        _text = text;
        _background = background;

        _progress = animate && _points.Count > 0 ? 0 : 1;

        BuildBrushes();
        BuildTexts();
        InvalidateVisual();
    }

    private void BuildBrushes()
    {
        _faint = new SolidColorBrush(_text, 0.07);
        _label = new SolidColorBrush(_text, 0.52);
        _accentBrush = new SolidColorBrush(_accent);

        var peak = _points.Count > 0 ? _points.Max(p => p.Sleeping) : 0;

        // Y 轴刻度要"整"：2 个人睡着时轴顶就画到 2，别拿 4 把柱子压平；
        // 人数一多，刻度改成 5 或 10 一档，避免出现 0/9/18/26 这种读不出来的数
        var step = peak <= 5 ? 1 : peak <= 10 ? 2 : peak <= 30 ? 5 : 10;
        _max = Math.Max(peak, 1);
        _max = ((_max + step - 1) / step) * step;
        _steps = Math.Max(1, _max / step);

        _barBody = new IBrush[_points.Count];
        for (var i = 0; i < _points.Count; i++)
        {
            var ratio = _points[i].Sleeping / (double)_max;
            // 人越多颜色越实，"哪几根是峰值"一眼就能看出来
            _barBody[i] = new SolidColorBrush(_accent, 0.16 + 0.44 * Math.Min(1, ratio));
        }

        if (_tops.Length != _points.Count)
        {
            _tops = new Point[_points.Count];
            _grown = new bool[_points.Count];
        }

        _linePen = new Pen(_accentBrush, 1.35);
        _peakPen = new Pen(_accentBrush, 1, new DashStyle(new double[] { 2, 3 }, 0));

        var axis = new FormattedText[_steps + 1];
        for (var i = 0; i <= _steps; i++)
        {
            var value = _max * i / _steps;
            axis[i] = Make(value.ToString(), 10.5, _label);
        }

        _axisText = axis;
    }

    private void BuildTexts()
    {
        _startText = Make(_startLabel, 10.5, _label);
        _endText = Make(_endLabel, 10.5, _label);
        _emptyText = Make("这节课没有采样到数据", 12, _label);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 40 || bounds.Height < 40) return;

        const double padLeft = 34;
        const double padRight = 8;
        const double padTop = 12;
        const double padBottom = 22;

        var plot = new Rect(
            padLeft,
            padTop,
            Math.Max(10, bounds.Width - padLeft - padRight),
            Math.Max(10, bounds.Height - padTop - padBottom));

        // ---- 横向网格 + Y 轴数字 ----
        for (var i = 0; i <= _steps && i < _axisText.Length; i++)
        {
            var y = plot.Bottom - plot.Height * i / _steps;
            context.FillRectangle(_faint, new Rect(plot.X, y, plot.Width, 1));

            var label = _axisText[i];
            context.DrawText(label, new Point(plot.X - label.Width - 7, y - label.Height / 2));
        }

        if (_points.Count == 0)
        {
            DrawEmpty(context, plot);
            return;
        }

        // ---- 柱子：从左到右依次弹出 ----
        var count = _points.Count;
        var slot = plot.Width / count;
        var barWidth = Math.Max(2, Math.Min(13, slot - 2.0));

        var topPoints = _tops;
        var grown = _grown;

        if (topPoints.Length < count)
        {
            topPoints = _tops = new Point[count];
            grown = _grown = new bool[count];
        }

        // 每根柱子占时间轴的一小段，前面的先长，整体像一道 wave 从左边扫过去。
        // 起点间距铺满 58% 的时间轴，剩下 42% 留给每根柱子自己长，
        // 这样进度 50% 时右半边还没开始，扫过去的动作才看得出来
        var stagger = count > 1 ? 0.58 / (count - 1) : 0;

        for (var i = 0; i < count; i++)
        {
            var local = Ease.OutCubic(Ease.Segment(_progress, i * stagger, i * stagger + 0.42));

            var ratio = _points[i].Sleeping / (double)_max;
            var height = plot.Height * ratio * local;

            var x = plot.X + slot * i + (slot - barWidth) / 2;
            var y = plot.Bottom - height;

            if (height >= 0.6)
            {
                context.FillRectangle(_barBody[i], new Rect(x, y, barWidth, height));

                // 顶部盖一格实心像素，像 LED 电平表
                context.FillRectangle(_accentBrush, new Rect(x, y, barWidth, Math.Min(2.5, height)));
            }

            topPoints[i] = new Point(x + barWidth / 2, y);
            grown[i] = local > 0.98;
        }

        // ---- 折线：只连已经长完的柱子，避免线头飘在空中 ----
        var ready = 0;
        while (ready < count && grown[ready]) ready++;

        if (ready > 1)
        {
            var geometry = new StreamGeometry();

            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(topPoints[0], false);
                for (var i = 1; i < ready; i++) ctx.LineTo(topPoints[i]);
                ctx.EndFigure(false);
            }

            context.DrawGeometry(null, _linePen, geometry);

            var hop = Math.Max(1, count / 7);
            for (var i = 0; i < ready; i += hop)
            {
                context.FillRectangle(_accentBrush,
                    new Rect(topPoints[i].X - 1.5, topPoints[i].Y - 1.5, 3, 3));
            }
        }

        // ---- 峰值标记：全部长完之后亮一下 ----
        if (_progress > 0.98 && count > 0)
        {
            var peakIndex = 0;
            for (var i = 1; i < count; i++)
            {
                if (_points[i].Sleeping > _points[peakIndex].Sleeping) peakIndex = i;
            }

            if (_points[peakIndex].Sleeping > 0 && _peakPen != null)
            {
                var top = topPoints[peakIndex];
                context.DrawLine(_peakPen, new Point(top.X, top.Y - 8), new Point(top.X, plot.Bottom));
                context.FillRectangle(_accentBrush, new Rect(top.X - 2.5, top.Y - 12, 5, 5));
            }
        }

        // ---- 底部时间 ----
        if (_startText != null)
        {
            context.DrawText(_startText, new Point(plot.X, plot.Bottom + 5));
        }

        if (_endText != null)
        {
            context.DrawText(_endText, new Point(plot.Right - _endText.Width, plot.Bottom + 5));
        }
    }

    private void DrawEmpty(DrawingContext context, Rect plot)
    {
        for (var x = plot.X; x < plot.Right; x += 6)
        {
            context.FillRectangle(_faint, new Rect(x, plot.Bottom - 1, 3, 1));
        }

        if (_emptyText == null) return;

        context.DrawText(_emptyText, new Point(
            plot.X + (plot.Width - _emptyText.Width) / 2,
            plot.Y + plot.Height / 2 - _emptyText.Height / 2));
    }

    private static FormattedText Make(string value, double size, IBrush brush)
        => new(
            value ?? "",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Microsoft YaHei"),
            size,
            brush);
}
