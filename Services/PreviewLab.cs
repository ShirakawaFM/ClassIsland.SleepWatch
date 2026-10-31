using System;
using System.Collections.Generic;
using System.Linq;

using ClassIsland.SleepWatch.Views;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 结果试弹。
///
/// 一节课 45 分钟，等不起。所以给两个手动入口：
/// <list type="bullet">
/// <item><b>真实数据</b> —— 直接拿账本里已有的记录弹，验证"下课之后到底长什么样"；</item>
/// <item><b>合成数据</b> —— 用固定种子现场造几天，改一次配色就能重弹一次，方便调视觉。</item>
/// </list>
/// 两种都<b>不写历史</b>，弹一百次也不会污染 history.json。
///
/// 种子单独管：<see cref="Reroll"/> 换一个种子再弹，就能连着看不同的人数曲线，
/// 不用关窗口重开。
/// </summary>
public static class PreviewLab
{
    private static readonly Random Dice = new();

    /// <summary>合成数据的随机种子。同一个种子造出来的数据完全一样，方便对照。</summary>
    public static int Seed { get; private set; } = 20261031;

    /// <summary>上一次弹的是不是合成数据。</summary>
    public static bool LastWasSample { get; private set; }

    /// <summary>上一次试弹的情况，设置页会显示出来。</summary>
    public static string LastNote { get; private set; } = "还没试弹过";

    public static int OpenCount { get; private set; }

    /// <summary>打开总结窗。返回是否真的弹了。</summary>
    public static bool Open(bool sample)
    {
        try
        {
            var cfg = SummarySettingsStore.Load();
            var detection = SettingsStore.Load();

            var days = sample
                ? SampleData.BuildDays(Math.Clamp(cfg.HistoryDays, 3, 6), Seed)
                : SleepLedger.Instance.RecentDays(cfg.HistoryDays, DateTime.Now);

            // 真实数据一条都没有时，别弹一个空壳出来，直接落到示例并说明原因
            var empty = !sample && days.All(d => d.Lessons.All(l => l.Episodes.Count == 0));

            if (empty)
            {
                days = SampleData.BuildDays(Math.Clamp(cfg.HistoryDays, 3, 6), Seed);
                LastNote = "账本里还没有记录，这次先用合成数据给你看效果。";
                sample = true;
            }
            else
            {
                LastNote = sample
                    ? $"合成数据 · 种子 {Seed} · {days.Count} 天"
                    : $"真实记录 · {days.Count} 天 · 共 {days.Sum(d => d.Lessons.Count)} 节";
            }

            // 页序跟窗口里完全一致：上节课 → 前两天 → 总计
            var pages = days
                .Take(3)
                .Select((d, i) => SummaryBuilder.Build(d, days, cfg, detection.ClassSize, detection.SleepSeconds, i == 0))
                .ToList();

            pages.Add(SummaryBuilder.BuildTotal(days, cfg, detection.ClassSize));

            LastWasSample = sample;
            OpenCount++;

            SummaryWindow.ShowPages(cfg, pages, sample);
            return true;
        }
        catch (Exception ex)
        {
            LastNote = "试弹失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>换一个种子，再造一批数据弹出来。只影响合成数据。</summary>
    public static bool Reroll()
    {
        Seed = Dice.Next(1, int.MaxValue);
        return Open(sample: true);
    }

    /// <summary>换一个种子并返回。窗口里点「换一版」用它，原地重画不重开窗。</summary>
    public static int NextSeed()
    {
        Seed = Dice.Next(1, int.MaxValue);
        return Seed;
    }

    public static string Describe()
        => LastWasSample
            ? $"当前是合成数据（种子 {Seed}），点【换一版】会重新造一批。"
            : "当前是账本里的真实记录。";
}
