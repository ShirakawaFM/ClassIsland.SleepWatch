using System;
using System.Collections.Generic;

using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 课表事件桥。上课自动开、下课自动停，省心也省电。
///
/// 用"按名字订阅"而不是单一回调：组件、设置页、课后总结三处都要听下课铃，
/// 各自带 key 订阅一次，重复调用只会覆盖自己那一份，不会互相顶掉。
/// 全部包在 try/catch 里：拿不到课表服务时插件照样能手动用。
/// </summary>
public static class LessonBridge
{
    private sealed class Subscription
    {
        public Action? OnClass { get; init; }
        public Action? OnBreak { get; init; }
        public Action? OnAfterSchool { get; init; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Subscription> Subscribers = new();

    private static bool _attached;

    /// <summary>
    /// 订阅课表事件。同一个 key 再调一次会替换掉上一次的三段回调。
    /// </summary>
    public static void Subscribe(string key, Action? onClass = null, Action? onBreak = null,
        Action? onAfterSchool = null)
    {
        lock (Gate)
        {
            Subscribers[key] = new Subscription
            {
                OnClass = onClass,
                OnBreak = onBreak,
                OnAfterSchool = onAfterSchool
            };
        }

        EnsureAttached();
    }

    /// <summary>老接口，等价于用固定 key 订阅。</summary>
    public static void Attach(Action? onClass, Action? onBreak, Action? onAfterSchool)
        => Subscribe("legacy", onClass, onBreak, onAfterSchool);

    private static void EnsureAttached()
    {
        if (_attached) return;

        try
        {
            var lessons = IAppHost.GetService<ILessonsService>();
            if (lessons == null) return;

            lessons.OnClass += (_, _) => Raise(s => s.OnClass);
            lessons.OnBreakingTime += (_, _) => Raise(s => s.OnBreak);
            lessons.OnAfterSchool += (_, _) => Raise(s => s.OnAfterSchool);

            _attached = true;
        }
        catch
        {
            // 课表服务不可用，忽略；下次调用还会再试
        }
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
            try
            {
                handler();
            }
            catch
            {
                // 事件回调里不要抛，一个订阅者出错不能拖累别人
            }
        }
    }
}
