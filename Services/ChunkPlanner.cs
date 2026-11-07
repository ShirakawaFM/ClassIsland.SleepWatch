using System;

using OpenCvSharp;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 分块推理的调度。
///
/// 教室画面里，天花板、墙壁、走道占了大半，全图推理等于把算力撒在这些地方。
/// 把画面切成 2×2 四块、块间留 15% 重叠，每帧只算一块，四帧覆盖一遍全画面：
/// 等效帧率不掉，单帧推理耗时却降到四分之一左右。
///
/// 另一层收益是精度：同样的 640 输入，喂的是一半画面的裁剪，等效分辨率翻倍，
/// 后排那些只有几十像素高的小目标也能被捞出来。
///
/// 已经跟踪到目标的块优先算；连着两轮没人的块隔一轮才看一次。
/// </summary>
public sealed class ChunkPlanner
{
    private readonly Rect[] _regions;
    private readonly int[] _idle;

    private int _cursor;

    public ChunkPlanner(int cols, int rows, double overlap)
    {
        cols = Math.Clamp(cols, 1, 3);
        rows = Math.Clamp(rows, 1, 3);
        overlap = Math.Clamp(overlap, 0, 0.4);

        _regions = Build(cols, rows, overlap);
        _idle = new int[_regions.Length];
    }

    public int Count => _regions.Length;

    /// <summary>下一次该看哪一块。</summary>
    public Rect Next(int width, int height, out int index)
    {
        if (_regions.Length == 1)
        {
            index = 0;
            return new Rect(0, 0, width, height);
        }

        for (var step = 0; step < _regions.Length; step++)
        {
            var i = (_cursor + step) % _regions.Length;

            // 连着两轮空着的块，隔一轮再扫，省下来的时间给有人的块
            if (_idle[i] >= 2 && step % 2 == 0 && step > 0) continue;

            _cursor = (i + 1) % _regions.Length;
            index = i;
            return ToPixels(_regions[i], width, height);
        }

        // 全都在歇：退回轮询，至少保证画面不会漏
        var fallback = _cursor;
        _cursor = (_cursor + 1) % _regions.Length;
        index = fallback;
        return ToPixels(_regions[fallback], width, height);
    }

    /// <summary>把这一块的检测结果回报上去，用来决定下一轮要不要跳过它。</summary>
    public void Report(int index, int found)
    {
        if (index < 0 || index >= _idle.Length) return;

        _idle[index] = found > 0 ? 0 : Math.Min(4, _idle[index] + 1);
    }

    public void Reset()
    {
        _cursor = 0;
        Array.Clear(_idle, 0, _idle.Length);
    }

    /// <summary>用比例表示每一块，换分辨率也不用重算。</summary>
    private static Rect[] Build(int cols, int rows, double overlap)
    {
        var list = new Rect[cols * rows];

        var w = 1.0 / cols;
        var h = 1.0 / rows;

        var padX = w * overlap;
        var padY = h * overlap;

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var x0 = Math.Max(0, c * w - padX);
                var y0 = Math.Max(0, r * h - padY);
                var x1 = Math.Min(1, (c + 1) * w + padX);
                var y1 = Math.Min(1, (r + 1) * h + padY);

                list[r * cols + c] = new Rect(
                    (int)(x0 * 10000), (int)(y0 * 10000),
                    (int)((x1 - x0) * 10000), (int)((y1 - y0) * 10000));
            }
        }

        return list;
    }

    private static Rect ToPixels(Rect ratio, int width, int height)
    {
        var x = (int)(ratio.X / 10000.0 * width);
        var y = (int)(ratio.Y / 10000.0 * height);
        var w = Math.Max(16, (int)(ratio.Width / 10000.0 * width));
        var h = Math.Max(16, (int)(ratio.Height / 10000.0 * height));

        return new Rect(x, y, Math.Min(w, width - x), Math.Min(h, height - y));
    }
}
