using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using ClassIsland.SleepWatch.Services;

namespace ClassIsland.SleepWatch.Controls;

/// <summary>
/// 人数变化曲线。故意用柱状像素条而不是平滑折线 —— 跟整页的像素风格统一，
/// 而且 20 秒一个采样点本来就是离散的，画成平滑曲线反而在撒谎。
/// </summary>
public sealed class SleepCurveChart : Control
{
    private IReadOnlyList<CurvePoint> _points = Array.Empty<CurvePoint>();
    private string _startLabel = "";
    private string _endLabel = "";
    private Color _accent = Color.Parse("#D97757");
    private Color _text = Color.Parse("#2E2A26");
    private Color _background = Color.Parse("#FFFCF6");

    private readonly DispatcherTimer _grow;
    private double _progress = 1;

    public SleepCurveChart()
    {
        ClipToBounds = true;

        _grow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(28) };
        _grow.Tick += (_, _) =>
        {
            _progress += 0.11;
            if (_progress >= 1)
            {
                _progress = 1;
                _grow.Stop();
            }

            InvalidateVisual();
        };
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
        _startLabel = startLabel;
        _endLabel = endLabel;
        _accent = accent;
        _text = text;
        _background = background;

        if (animate && _points.Count > 0)
        {
            _progress = 0;
            _grow.Start();
        }
        else
        {
            _progress = 1;
            _grow.Stop();
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 40 || bounds.Height < 40) return;

        var padLeft = 38.0;
        var padRight = 10.0;
        var padTop = 14.0;
        var padBottom = 24.0;

        var plot = new Rect(
            padLeft,
            padTop,
            Math.Max(10, bounds.Width - padLeft - padRight),
            Math.Max(10, bounds.Height - padTop - padBottom));

        var textBrush = new SolidColorBrush(_text, 0.55);
        var faintBrush = new SolidColorBrush(_text, 0.08);
        var accentBrush = new SolidColorBrush(_accent);

        if (_points.Count == 0)
        {
            DrawEmpty(context, bounds, plot, textBrush);
            return;
        }

        var peak = Math.Max(1, _points.Max(p => p.Sleeping));

        // Y 轴按峰值取整：只有 2 个人睡着时，轴顶就画到 2，别拿 4 把柱子压平
        var max = peak <= 5 ? Math.Max(2, peak) : ((peak + 4) / 5) * 5;

        // ---- 横向网格 + Y 轴数字 ----
        var steps = Math.Min(4, max);
        for (var i = 0; i <= steps; i++)
        {
            var y = plot.Bottom - plot.Height * i / steps;
            context.FillRectangle(faintBrush, new Rect(plot.X, y, plot.Width, 1));

            var value = max * i / steps;
            var label = Text(value.ToString(), 10.5, textBrush);
            context.DrawText(label, new Point(plot.X - label.Width - 8, y - label.Height / 2));
        }

        // ---- 柱子 ----
        var count = _points.Count;
        var slot = plot.Width / count;
        var barWidth = Math.Max(2, Math.Min(14, slot - 2.0));

        var topPoints = new List<Point>(count);

        for (var i = 0; i < count; i++)
        {
            var value = _points[i].Sleeping;
            var ratio = value / (double)max;
            var height = plot.Height * ratio * _progress;

            var x = plot.X + slot * i + (slot - barWidth) / 2;
            var y = plot.Bottom - height;

            if (height >= 1)
            {
                var body = new SolidColorBrush(_accent, 0.20 + 0.35 * ratio);
                context.FillRectangle(body, new Rect(x, y, barWidth, height));

                // 顶部盖一格实心像素，像 LED 电平表
                context.FillRectangle(accentBrush, new Rect(x, y, barWidth, Math.Min(3, height)));
            }

            topPoints.Add(new Point(x + barWidth / 2, y));
        }

        // ---- 折线，把柱子顶连起来 ----
        if (topPoints.Count > 1 && _progress >= 1)
        {
            var pen = new Pen(accentBrush, 1.4, new DashStyle(null, 0));
            var geometry = new StreamGeometry();

            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(topPoints[0], false);
                for (var i = 1; i < topPoints.Count; i++) ctx.LineTo(topPoints[i]);
                ctx.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);

            // 每 6 个点放一枚像素方块做节点
            for (var i = 0; i < topPoints.Count; i += Math.Max(1, topPoints.Count / 8))
            {
                context.FillRectangle(new SolidColorBrush(_accent, 0.9),
                    new Rect(topPoints[i].X - 1.5, topPoints[i].Y - 1.5, 3, 3));
            }
        }

        // ---- 底部时间 ----
        var startLabel = Text(_startLabel, 10.5, textBrush);
        var endLabel = Text(_endLabel, 10.5, textBrush);

        context.DrawText(startLabel, new Point(plot.X, plot.Bottom + 6));
        context.DrawText(endLabel, new Point(plot.Right - endLabel.Width, plot.Bottom + 6));
    }

    private void DrawEmpty(DrawingContext context, Rect bounds, Rect plot, IBrush brush)
    {
        // 虚线基线
        for (var x = plot.X; x < plot.Right; x += 6)
        {
            context.FillRectangle(brush, new Rect(x, plot.Bottom - 1, 3, 1));
        }

        var label = Text("这节课没有采样到数据", 12, brush);
        context.DrawText(label, new Point(
            plot.X + (plot.Width - label.Width) / 2,
            plot.Y + plot.Height / 2 - label.Height / 2));
    }

    private FormattedText Text(string value, double size, IBrush brush)
    {
        return new FormattedText(
            value,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Microsoft YaHei"),
            size,
            brush);
    }
}
