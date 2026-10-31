using System;
using System.Collections.Generic;

using Avalonia.Controls;
using Avalonia.Threading;

namespace ClassIsland.SleepWatch.Controls;

/// <summary>
/// 一个跟着显示器刷新走的帧驱动器。
///
/// 为什么不用 <see cref="DispatcherTimer"/>：它按「至少隔 N 毫秒」调度，跟垂直同步对不上，
/// 16ms 的定时器实际会抖成 16/32/48 交替出现，看起来就是一顿一顿的。
/// <see cref="TopLevel.RequestAnimationFrame"/> 则是在每一帧真正要合成之前回调，
/// 天然和 60Hz 对齐，而且时间戳是真实的帧时间 —— 动画按时间推进，掉帧也不会变慢。
///
/// 用法：new FrameDriver(topLevel)，然后 OnFrame += ...。停止时 Dispose。
/// </summary>
public sealed class FrameDriver : IDisposable
{
    private readonly TopLevel _top;
    private readonly Action<TimeSpan> _callback;
    private readonly HashSet<object> _owners = new();

    private bool _running;
    private bool _disposed;
    private TimeSpan _start;

    public FrameDriver(TopLevel top, Action<TimeSpan> callback)
    {
        _top = top;
        _callback = callback;
    }

    /// <summary>从启动到现在的时长，给动画算进度用。</summary>
    public TimeSpan Elapsed { get; private set; }

    /// <summary>最近一帧的间隔，用来统计实际帧率。</summary>
    public double LastFrameMs { get; private set; }

    public void Start()
    {
        if (_running || _disposed) return;

        _running = true;
        _start = TimeSpan.Zero;
        Request();
    }

    public void Stop() => _running = false;

    private void Request()
    {
        if (!_running || _disposed) return;

        _top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan timestamp)
    {
        if (!_running || _disposed) return;

        if (_start == TimeSpan.Zero)
        {
            _start = timestamp;
            Elapsed = TimeSpan.Zero;
        }
        else
        {
            var delta = timestamp - _start;
            LastFrameMs = (delta - Elapsed).TotalMilliseconds;
            Elapsed = delta;
        }

        try
        {
            _callback(Elapsed);
        }
        catch
        {
            // 单帧出错不要停掉整条动画
        }

        Request();
    }

    public void Dispose()
    {
        _disposed = true;
        _running = false;
    }
}

/// <summary>缓动函数。都返回 0~1。</summary>
public static class Ease
{
    /// <summary>开头快、结尾慢。数字滚动和柱子生长都用它。</summary>
    public static double OutCubic(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;

        var u = 1 - t;
        return 1 - u * u * u;
    }

    /// <summary>带一点点回弹，用在卡片入场，别太夸张。</summary>
    public static double OutBack(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;

        const double c = 1.24;
        var u = t - 1;
        return 1 + (c + 1) * u * u * u + c * u * u;
    }

    /// <summary>把 t 映射到 [from, to] 区间内的进度。</summary>
    public static double Segment(double t, double from, double to)
    {
        if (to <= from) return t >= to ? 1 : 0;

        var v = (t - from) / (to - from);
        return v <= 0 ? 0 : v >= 1 ? 1 : v;
    }
}
