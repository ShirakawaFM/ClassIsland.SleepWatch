using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using ClassIsland.Shared.Models.Profile;

using ClassIsland.SleepWatch.Services;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 课表同步。
///
/// 数据来源是 <see cref="ILessonsService.GetClassPlanByDate"/>——这是接口上的公开方法，
/// 不需要反射；只有「当前科目标题」这类纯展示字段才用反射兜底读，读不到就不显示。
///
/// 同步策略：每 <see cref="RefreshSeconds"/> 秒读一次，跨天强制重读。
/// 读失败不清空上一次成功的结果——课表服务偶尔抽风时，宁可继续用旧数据，
/// 也不要让正在上的那一节课凭空消失。
/// </summary>
public static class TimetableSync
{
    /// <summary>常规重读间隔。课表改动本身有事件，但事件不全，加个兜底轮询更省事。</summary>
    private const int RefreshSeconds = 20;

    private static readonly object Gate = new();

    public static SyncState State { get; private set; } = SyncState.NotReady;

    /// <summary>给人看的说明，直接扔到设置页上。</summary>
    public static string Message { get; private set; } = "还没读过课表";

    /// <summary>当天课表。读失败时保留上一次的结果。</summary>
    public static ScheduleDay? Today { get; private set; }

    public static DateTime LastAttempt { get; private set; } = DateTime.MinValue;

    /// <summary>最近一次成功同步的时间。</summary>
    public static DateTime LastSuccess { get; private set; } = DateTime.MinValue;

    /// <summary>课表能否用来自动上下课：读到了、有内容、且没被停用。</summary>
    public static bool IsUsable => State == SyncState.Ok && Today != null && Today.Blocks.Count > 0;

    // ------------------------------------------------------------------
    // 时间
    // ------------------------------------------------------------------

    /// <summary>
    /// 当前时间。优先用 ClassIsland 的精确时间服务（它会做网络校时），
    /// 拿不到就退回系统时间。所有判定都走这里，保证同一套时钟。
    /// </summary>
    public static DateTime Now()
    {
        try
        {
            var exact = IAppHost.GetService<IExactTimeService>();
            if (exact != null)
            {
                var now = exact.GetCurrentLocalDateTime();
                if (now.Year > 2000) return now;
            }
        }
        catch
        {
            // 精确时间服务不可用就用系统时间，不影响功能
        }

        return DateTime.Now;
    }

    // ------------------------------------------------------------------
    // 同步
    // ------------------------------------------------------------------

    /// <summary>
    /// 重新读课表。<paramref name="force"/> 为 false 时受 <see cref="RefreshSeconds"/> 节流，
    /// 状态机每秒调一次也不会真的每秒读一遍。
    /// </summary>
    public static void Refresh(bool force = false)
    {
        lock (Gate)
        {
            var now = Now();

            var sameDay = Today != null && Today.Date == now.Date;
            var throttled = (now - LastAttempt).TotalSeconds < RefreshSeconds;

            if (!force && sameDay && throttled) return;

            LastAttempt = now;

            // 跨天时旧课表必须作废，否则第二天的判定会拿昨天的时段来套
            if (!sameDay) Today = null;

            try
            {
                Read(now);
            }
            catch (Exception ex)
            {
                State = SyncState.Failed;
                Message = "读课表出错：" + ex.Message;
            }
        }
    }

    private static void Read(DateTime now)
    {
        ILessonsService? lessons;

        try
        {
            lessons = IAppHost.GetService<ILessonsService>();
        }
        catch
        {
            lessons = null;
        }

        if (lessons == null)
        {
            State = SyncState.NoService;
            Message = "拿不到课表服务，自动上下课暂时不可用（可以直接手动启停监测）。";
            return;
        }

        ClassPlan? plan;

        try
        {
            plan = lessons.GetClassPlanByDate(now.Date);
        }
        catch (Exception ex)
        {
            State = SyncState.Failed;
            Message = "课表读取异常：" + ex.Message;
            return;
        }

        if (plan == null)
        {
            State = SyncState.NoPlan;
            Message = "今天没有排课（周末、假期，或者还没在 ClassIsland 里配置课表）。";
            Today = null;
            return;
        }

        var blocks = Normalize(plan);
        var day = new ScheduleDay
        {
            Date = now.Date,
            PlanName = string.IsNullOrWhiteSpace(plan.Name) ? "默认课表" : plan.Name!,
            PlanEnabled = plan.IsEnabled,
            Blocks = blocks,
            SyncedAt = now
        };

        if (blocks.Count == 0)
        {
            // 课表在，但时间段是空的：可能刚新建还没填
            State = SyncState.Empty;
            Message = plan.IsEnabled
                ? $"「{day.PlanName}」里没有任何时间段，去 ClassIsland 的时间表里加几节。"
                : $"「{day.PlanName}」没有启用，自动上下课已暂停。";
            Today = day;
            return;
        }

        if (!plan.IsEnabled)
        {
            // 时间段读到了，但课表被停用。ClassIsland 自己也不会切换状态，我们跟着停。
            State = SyncState.Disabled;
            Message = $"「{day.PlanName}」当前未启用，自动上下课已暂停；时间表仍然可以查看。";
            Today = day;
            LastSuccess = now;
            return;
        }

        State = SyncState.Ok;
        Message = $"已同步「{day.PlanName}」· {day.ClassCount} 节课 · {day.FirstStart:hh\\:mm}–{day.LastEnd:hh\\:mm}";
        Today = day;
        LastSuccess = now;
    }

    /// <summary>把 ClassIsland 的时间点列表翻成我们的块列表。</summary>
    private static List<ScheduleBlock> Normalize(ClassPlan plan)
    {
        var blocks = new List<ScheduleBlock>();

        var items = plan.ValidTimeLayoutItems;
        if (items == null || items.Count == 0) return blocks;

        var subjects = ResolveSubjectNames(plan);

        var index = 0;

        foreach (var item in items.OrderBy(i => i.StartTime))
        {
            // 0 上课 / 1 课间 / 2 分割线 / 3 行动。后两种是零长度的标记，不是时间段
            if (item.TimeType is 2 or 3) continue;

            var start = item.StartTime;
            var end = item.EndTime;

            // 起始晚于结束、或者零长度，都是脏数据，跳过而不是让后面算崩
            if (end <= start) continue;

            if (item.TimeType == 0)
            {
                index++;

                blocks.Add(new ScheduleBlock
                {
                    Index = index,
                    Kind = BlockKind.Class,
                    Start = start,
                    End = end,
                    Label = subjects.TryGetValue(index, out var name) ? name : ""
                });
            }
            else
            {
                blocks.Add(new ScheduleBlock
                {
                    Index = 0,
                    Kind = BlockKind.Break,
                    Start = start,
                    End = end,
                    Label = string.IsNullOrWhiteSpace(item.BreakNameText) ? "课间" : item.BreakNameText
                });
            }
        }

        return blocks;
    }

    /// <summary>第几节 → 科目名。取不到就留空，界面会退化成「第 N 节」。</summary>
    private static Dictionary<int, string> ResolveSubjectNames(ClassPlan plan)
    {
        var result = new Dictionary<int, string>();

        try
        {
            var profile = IAppHost.GetService<Profile>();
            if (profile == null) return result;

            foreach (var info in plan.Classes)
            {
                if (!info.IsEnabled) continue;
                if (!profile.Subjects.TryGetValue(info.SubjectId, out var subject)) continue;

                var name = subject?.Name;
                if (!string.IsNullOrWhiteSpace(name)) result[info.Index] = name!;
            }
        }
        catch
        {
            // 科目名只是锦上添花，读不到就算了
        }

        return result;
    }

    // ------------------------------------------------------------------
    // 反射读几个纯展示字段
    // ------------------------------------------------------------------

    /// <summary>
    /// 读 ClassIsland 自己的相位（<c>LessonsService.CurrentState</c>）。
    /// 只用来在界面上跟我们的判断互相印证，不参与任何决策。
    /// </summary>
    public static string? ReadNativeStateName()
    {
        try
        {
            var lessons = IAppHost.GetService<ILessonsService>();
            if (lessons == null) return null;

            var prop = lessons.GetType().GetProperty("CurrentState", BindingFlags.Public | BindingFlags.Instance);
            var value = prop?.GetValue(lessons);

            return value?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>给设置页写同步状态用的一行字。</summary>
    public static string Describe()
    {
        var synced = LastSuccess == DateTime.MinValue
            ? "还没成功同步过"
            : $"上次同步 {LastSuccess:HH:mm:ss}";

        return $"{Message}（{synced}）";
    }
}
