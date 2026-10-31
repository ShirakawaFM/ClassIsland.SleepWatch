using System;

using Avalonia.Threading;

using ClassIsland.SleepWatch.Views;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 课后总结的调度器。
///
/// 三件事：上课时给账本开一页、下课把这一页结掉并且（按配置）弹总结、
/// 隔几分钟把还没落盘的记录写一次硬盘。
/// </summary>
public static class SummaryHost
{
    private static bool _attached;
    private static DispatcherTimer? _flushTimer;

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;

        LessonBridge.Subscribe(
            "summary",
            onClass: OnClass,
            onBreak: OnBreak,
            onAfterSchool: OnBreak);

        // 上课途中也定时落盘：教室电脑被直接断电的概率不低
        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _flushTimer.Tick += (_, _) => SleepLedger.Instance.FlushIfDirty();
        _flushTimer.Start();
    }

    private static void OnClass()
    {
        try
        {
            SleepLedger.Instance.BeginLesson(DateTime.Now, SettingsStore.Load().ClassSize);
        }
        catch
        {
            // 记账失败不能影响上课
        }
    }

    private static void OnBreak()
    {
        try
        {
            var lesson = SleepLedger.Instance.EndLesson(DateTime.Now);

            // 一个人都没睡就别弹了，安静地翻过去
            if (lesson == null || lesson.Episodes.Count == 0) return;

            var cfg = SummarySettingsStore.Load();
            if (!cfg.AutoPopupOnLessonEnd) return;

            SummaryWindow.Show(cfg, sample: false);
        }
        catch
        {
            // 弹窗失败不能影响下课
        }
    }

    /// <summary>设置页里手动打开（示例数据用来调配色）。</summary>
    public static void ShowNow(bool sample)
    {
        SummaryWindow.Show(SummarySettingsStore.Load(), sample);
    }
}
