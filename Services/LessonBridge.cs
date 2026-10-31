using System;
using System.Collections.Generic;

using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 课表事件桥：把 <see cref="LessonClock"/> 的判定结果，分发给组件、设置页、课后总结三处。
///
/// 用「按名字订阅」而不是单一回调：三个订阅点各自带 key 订阅一次，
/// 重复调用只覆盖自己那一份，不会互相顶掉（早期用单一回调时，设置页一打开就把组件的订阅吃掉了）。
///
/// 两路输入：
/// <list type="number">
/// <item><b>时钟</b>（主）：<see cref="LessonClock"/> 按课表时间每秒判定，最可靠；</item>
/// <item><b>课表事件</b>（辅）：ClassIsland 的 OnClass / OnBreakingTime 到了就立刻催一次判定，
/// 把「最多 1 秒」的延迟压到毫秒；同时它是课表读不到时的兜底来源。</item>
/// </list>
/// </summary>
public static class LessonBridge
{
    private sealed class Subscription
    {
        public Action? OnClass { get; init; }
        public Action? OnBreak { get; init; }
        public Action? OnAfterSchool { get; init; }

        /// <summary>带结束原因的回调。课后总结用它决定要不要弹窗。</summary>
        public Action<LessonContext, LessonEndReason>? OnFinished { get; init; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Subscription> Subscribers = new();

    private static bool _clockHooked;
    private static bool _nativeHooked;

    /// <summary>
    /// 订阅课表事件。同一个 key 再调一次会替换掉上一次的回调。
    /// </summary>
    public static void Subscribe(
        string key,
        Action? onClass = null,
        Action? onBreak = null,
        Action? onAfterSchool = null,
        Action<LessonContext, LessonEndReason>? onFinished = null)
    {
        lock (Gate)
        {
            Subscribers[key] = new Subscription
            {
                OnClass = onClass,
                OnBreak = onBreak,
                OnAfterSchool = onAfterSchool,
                OnFinished = onFinished
            };
        }

        EnsureAttached();
    }

    /// <summary>老接口，等价于用固定 key 订阅。</summary>
    public static void Attach(Action? onClass, Action? onBreak, Action? onAfterSchool)
        => Subscribe("legacy", onClass, onBreak, onAfterSchool);

    private static void EnsureAttached()
    {
        // 有人订阅课表事件，说明插件要干活了，状态机也跟着起来
        LessonClock.EnsureStarted();

        HookClock();

        if (_nativeHooked) return;

        try
        {
            var lessons = IAppHost.GetService<ILessonsService>();
            if (lessons == null) return;

            // 事件只是"催一拍"，真正的判定还是交给状态机
            lessons.OnClass += (_, _) => Nudge(expectClass: true);
            lessons.OnBreakingTime += (_, _) => Nudge(expectClass: false);
            lessons.OnAfterSchool += (_, _) => Nudge(expectClass: false);

            _nativeHooked = true;
        }
        catch
        {
            // 课表服务不可用：时钟那条路照样能跑；下次调用还会再试
        }
    }

    private static void HookClock()
    {
        if (_clockHooked) return;
        _clockHooked = true;

        LessonClock.LessonStarted += _ => Raise(s => s.OnClass);

        LessonClock.LessonEnded += (context, reason) =>
        {
            Raise(s => s.OnFinished, context, reason);

            switch (reason)
            {
                case LessonEndReason.SchoolOver:
                    Raise(s => s.OnAfterSchool);
                    break;

                case LessonEndReason.Wake:
                    // 休眠补结算：只是把账收住，别惊动用户
                    break;

                default:
                    Raise(s => s.OnBreak);
                    break;
            }
        };
    }

    /// <summary>
    /// 课表事件到达：先让时钟立刻判一次。
    /// 如果时钟没认（比如课表读不到、或者相位本来就对），才用事件本身兜底，
    /// 这样既不会漏掉，也不会同一次下课触发两遍。
    /// </summary>
    private static void Nudge(bool expectClass)
    {
        var before = LessonClock.Phase;

        var changed = LessonClock.PulseNow();

        var already = expectClass
            ? before == LessonPhase.InClass
            : before != LessonPhase.InClass;

        if (changed || already) return;

        if (expectClass) Raise(s => s.OnClass);
        else Raise(s => s.OnBreak);
    }

    private static void Raise(Func<Subscription, Action?> pick)
    {
        List<Action> handlers;

        lock (Gate)
        {
            handlers = new List<Action>(Subscribers.Count);

            foreach (var sub in Subscribers.Values)
            {
                var handler = pick(sub);
                if (handler != null) handlers.Add(handler);
            }
        }

        foreach (var handler in handlers)
        {
            Try(handler);
        }
    }

    private static void Raise(Func<Subscription, Action<LessonContext, LessonEndReason>?> pick,
        LessonContext context, LessonEndReason reason)
    {
        List<Action<LessonContext, LessonEndReason>> handlers;

        lock (Gate)
        {
            handlers = new List<Action<LessonContext, LessonEndReason>>(Subscribers.Count);

            foreach (var sub in Subscribers.Values)
            {
                var handler = pick(sub);
                if (handler != null) handlers.Add(handler);
            }
        }

        foreach (var handler in handlers)
        {
            Try(() => handler(context, reason));
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // 事件回调里不要抛：一个订阅者出错不能拖累别人
        }
    }
}
