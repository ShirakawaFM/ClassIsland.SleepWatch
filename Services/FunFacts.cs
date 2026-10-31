using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassIsland.SleepWatch.Services;

/// <summary>一条趣味条目。Tag 是左上角那枚小标签，Text 是正文。</summary>
public sealed class FactItem
{
    public string Tag { get; init; } = "";
    public string Text { get; init; } = "";
    public int Index { get; init; }
}

/// <summary>
/// 把一节课的记录翻译成人话。
///
/// 原则：只用真实算得出来的数，凑不够 20 条就少几条，不编造、不注水。
/// 语气按"同学之间互相调侃"来写，不针对具体人（本来也认不出是谁）。
/// </summary>
public static class FunFactWriter
{
    public static List<FactItem> Build(
        LessonRecord lesson,
        DayRecord day,
        IReadOnlyList<DayRecord> history,
        int classSize,
        double thresholdSeconds,
        int max = 20)
    {
        var list = new List<string>();

        if (lesson.Episodes.Count == 0)
        {
            return new List<FactItem>
            {
                new() { Tag = "空", Text = "这节课一个人都没睡。要么讲得太精彩，要么摄像头没拍到。" }
            };
        }

        var eps = lesson.Episodes;
        var asleep = eps.Where(e => e.Awake).ToList();
        var start = lesson.Start;

        string At(double offset) => start.AddSeconds(offset).ToString("HH:mm");
        string Len(double sec) => Duration(sec);
        double Off(SleepEpisode e) => Math.Max(0, (e.Start - start).TotalSeconds);

        // ---------- 1. 第一个倒下 ----------
        var first = eps.OrderBy(Off).First();
        list.Add($"第一个倒下的是 {At(Off(first))}，离上课 {Len(Off(first))}。");

        // ---------- 2. 峰值 ----------
        if (lesson.Curve.Count > 0)
        {
            var peak = lesson.Curve.OrderByDescending(p => p.Sleeping).First();
            if (peak.Sleeping > 0)
            {
                list.Add($"同时睡着最多的一次是 {peak.Sleeping} 人，出现在 {At(peak.OffsetSeconds)}。");
            }

            var minAwake = lesson.Curve.Where(p => p.Present > 0).Select(p => p.Present - p.Sleeping).DefaultIfEmpty(0).Min();
            if (minAwake > 0)
            {
                list.Add($"画面里清醒的人最少的时候只剩 {minAwake} 个。");
            }
        }

        // ---------- 3. 倒下节奏 ----------
        if (lesson.Length.TotalMinutes > 1)
        {
            var per = lesson.Length.TotalSeconds / Math.Max(1, eps.Count);
            list.Add($"平均每 {Len(per)} 就有一个人趴下，节奏稳得像打卡。");
        }

        // ---------- 4. 最长的一觉 ----------
        if (lesson.Longest is { } longest)
        {
            var anime = Math.Max(1, (int)Math.Round(longest.Seconds / 480.0));
            list.Add($"最长的一觉 {Len(longest.Seconds)}，够刷 {anime} 集泡面番。");
        }

        // ---------- 5. 睡神 ----------
        var top = eps.GroupBy(e => e.TrackId).OrderByDescending(g => g.Count()).First();
        if (top.Count() >= 2)
        {
            list.Add($"有个位置这节课睡了 {top.Count()} 次，醒了又睡，建议考虑换个座位。");
        }

        // ---------- 6. 回笼觉 ----------
        var repeats = eps.GroupBy(e => e.TrackId).Count(g => g.Count() > 1);
        if (repeats > 0)
        {
            list.Add($"{repeats} 个人睡了两轮以上，回笼觉熟练度很高。");
        }

        // ---------- 7. 抗困冠军 ----------
        var latest = eps.OrderByDescending(Off).First();
        if (Off(latest) > lesson.Length.TotalSeconds * 0.4)
        {
            list.Add($"最抗困的那位硬撑了 {Len(Off(latest))} 才倒下。");
        }

        // ---------- 8. 铃响时还没醒 ----------
        var stillAsleep = lesson.Episodes.Count(e => !e.Awake);
        if (stillAsleep > 0)
        {
            list.Add($"下课铃响的时候还有 {stillAsleep} 个人没醒，需要有人喊一声。");
        }

        // ---------- 9. 全班合计 ----------
        var totalMinutes = eps.Sum(e => e.Seconds) / 60.0;
        if (totalMinutes >= 10)
        {
            list.Add($"全班加起来睡了 {totalMinutes:0} 分钟，差不多 {Math.Max(1, (int)Math.Round(totalMinutes / 45))} 节自习课。");
        }

        // ---------- 10. 最后 5 分钟 ----------
        if (lesson.Length.TotalSeconds > 600)
        {
            var tailFrom = lesson.Length.TotalSeconds - 300;
            var tail = eps.Count(e => Off(e) >= tailFrom);
            if (tail > 0)
            {
                list.Add($"最后 5 分钟又倒下 {tail} 个。越到下课越困，这个规律比课表还准。");
            }
        }

        // ---------- 11. 集体复苏 ----------
        if (asleep.Count >= 2)
        {
            var group = asleep
                .GroupBy(e => e.End!.Value.ToString("HH:mm"))
                .OrderByDescending(g => g.Count())
                .First();

            if (group.Count() >= 2)
            {
                list.Add($"{group.Key} 有 {group.Count()} 个人几乎同时醒过来，像被同一个梦叫醒的。");
            }
        }

        // ---------- 12. 画面左右半场 ----------
        var xs = eps.Select(e => e.CenterX).Where(x => x > 0).OrderBy(x => x).ToList();
        if (xs.Count >= 6)
        {
            var mid = xs[xs.Count / 2];
            var left = eps.Count(e => e.CenterX > 0 && e.CenterX <= mid);
            var right = eps.Count(e => e.CenterX > mid);
            if (Math.Abs(left - right) >= 2)
            {
                var where = left > right ? "左半边" : "右半边";
                list.Add($"画面{where}倒了 {Math.Max(left, right)} 次，另外半边只有 {Math.Min(left, right)} 次。");
            }
        }

        // ---------- 13. 平均时长 ----------
        var avg = eps.Average(e => e.Seconds);
        list.Add($"平均每人睡了 {Len(avg)}，判定线按 {Len(thresholdSeconds)} 算。");

        // ---------- 14. 全程清醒 ----------
        var slept = eps.Select(e => e.TrackId).Distinct().Count();
        var observed = Math.Max(lesson.Curve.Select(p => p.Present).DefaultIfEmpty(0).Max(), slept);
        var never = Math.Max(0, observed - slept);
        if (never > 0)
        {
            list.Add($"有 {never} 个人从头到尾一次都没被判定睡着，本班最精神。");
        }

        // ---------- 15. 长睡的人 ----------
        var longSleepers = eps.Where(e => e.Seconds >= 600).Select(e => e.TrackId).Distinct().Count();
        if (longSleepers > 0)
        {
            list.Add($"{longSleepers} 个人一觉超过 10 分钟，这已经不是打盹了。");
        }

        // ---------- 16. 最短一觉 ----------
        var shortest = asleep.OrderBy(e => e.Seconds).FirstOrDefault();
        if (shortest != null && shortest.Seconds < 60)
        {
            list.Add($"最短的一次只睡了 {Len(shortest.Seconds)}，刚趴下就被叫起来了。");
        }

        // ---------- 17. 今日累计 ----------
        if (day.Lessons.Count > 1)
        {
            list.Add($"今天累计 {day.TotalEpisodes} 人次、{day.TotalMinutes:0} 分钟，后面几节会更困。");
        }

        // ---------- 18. 和昨天比 ----------
        var yesterday = history.FirstOrDefault(d => d.Date == day.Date.AddDays(-1));
        if (yesterday != null && yesterday.Lessons.Count > 0)
        {
            var delta = day.TotalEpisodes - yesterday.TotalEpisodes;
            if (Math.Abs(delta) >= 1)
            {
                var word = delta > 0 ? "多" : "少";
                list.Add($"和昨天比，今天{word}了 {Math.Abs(delta)} 人次趴桌。");
            }
            else if (delta == 0)
            {
                list.Add("和昨天一模一样的人次，作息规律得可怕。");
            }
        }

        // ---------- 19. 和上一节课比 ----------
        var prev = day.Lessons.Where(l => l.Start < lesson.Start).OrderByDescending(l => l.Start).FirstOrDefault();
        if (prev != null && prev.Episodes.Count > 0)
        {
            var delta = lesson.Sleepers - prev.Sleepers;
            if (Math.Abs(delta) >= 1)
            {
                var word = delta > 0 ? "多" : "少";
                list.Add($"比上一节{word}了 {Math.Abs(delta)} 个人，睡眠是会传染的。");
            }
        }

        // ---------- 20. 曲线形状 ----------
        if (lesson.Curve.Count >= 6)
        {
            var half = lesson.Curve.Count / 2;
            var head = lesson.Curve.Take(half).Average(p => p.Sleeping);
            var tail = lesson.Curve.Skip(half).Average(p => p.Sleeping);

            if (head < 0.6 && tail > head + 0.8)
            {
                list.Add("睡意是一路爬上去的，前半节还算清醒，后半节彻底失守。");
            }
            else if (head > tail + 0.8)
            {
                list.Add("开局就很惨，后半节反而缓过来了。大概是讲到有意思的地方了。");
            }
            else
            {
                list.Add("人数曲线基本是平的，从头困到尾，稳定输出。");
            }
        }

        // ---------- 21. 没看清的人 ----------
        if (classSize > 0)
        {
            var neverSeen = classSize - observed;
            if (neverSeen >= 3)
            {
                list.Add($"有 {neverSeen} 个座位整节课都没被看清（背对、被挡或太远），这些位置不计入统计。");
            }
        }

        // ---------- 22. 开学第一分钟 ----------
        if (Off(first) < 60)
        {
            list.Add($"上课第一分钟就有人趴下了，可能上一节就没睡够。");
        }

        return list
            .Distinct()
            .Take(max)
            .Select((text, i) => new FactItem
            {
                Index = i + 1,
                Tag = (i + 1).ToString("00"),
                Text = text
            })
            .ToList();
    }

    public static string Duration(double seconds)
    {
        if (seconds <= 0) return "0 秒";
        if (seconds < 60) return $"{Math.Round(seconds):0} 秒";

        var minutes = (int)(seconds / 60);
        var rest = (int)Math.Round(seconds - minutes * 60);

        if (rest >= 60)
        {
            minutes++;
            rest = 0;
        }

        if (minutes < 60)
        {
            return rest == 0 ? $"{minutes} 分" : $"{minutes} 分 {rest} 秒";
        }

        var hours = minutes / 60;
        return $"{hours} 小时 {minutes % 60} 分";
    }
}
