using System;

using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 课表事件桥。上课自动开、下课自动停，省心也省电。
/// 全部包在 try/catch 里：拿不到课表服务时插件照样能手动用。
/// </summary>
public static class LessonBridge
{
    private static bool _attached;
    private static Action? _onClass;
    private static Action? _onBreak;
    private static Action? _onAfterSchool;

    public static void Attach(Action? onClass, Action? onBreak, Action? onAfterSchool)
    {
        if (onClass != null) _onClass = onClass;
        if (onBreak != null) _onBreak = onBreak;
        if (onAfterSchool != null) _onAfterSchool = onAfterSchool;

        if (_attached) return;

        try
        {
            var lessons = IAppHost.GetService<ILessonsService>();
            if (lessons == null) return;

            lessons.OnClass += (_, _) => Safe(_onClass);
            lessons.OnBreakingTime += (_, _) => Safe(_onBreak);
            lessons.OnAfterSchool += (_, _) => Safe(_onAfterSchool);

            _attached = true;
        }
        catch
        {
            // 课表服务不可用，忽略
        }
    }

    private static void Safe(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch
        {
            // 事件回调里不要抛
        }
    }
}
