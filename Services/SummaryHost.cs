using System;
using System.Linq;

using Avalonia.Threading;

using ClassIsland.SleepWatch.Views;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 课后总结的调度器。
///
/// 挂三件事：上课时给账本开一页、下课把这一页结掉并且（按配置）弹总结、
/// 隔几分钟把还没落盘的记录写一次硬盘。
///
/// 上下课的时刻不在这里判断 —— 那是 <see cref="LessonClock"/> 的活，
/// 这里只负责"接到通知之后该干什么"。这样换判定逻辑不用动这里。
/// </summary>
public static class SummaryHost
{
    private static bool _attached;
    private static DispatcherTimer? _flushTimer;

    /// <summary>排着的那个"等几秒再弹"，只留最新一个。<c>RunOnce</c> 返回的是句柄，Dispose 即取消。</summary>
    private static IDisposable? _popupTimer;

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;

        LessonBridge.Subscribe(
            "summary",
            onClass: OnClass,
            onFinished: OnFinished);

        // 上课途中也定时落盘：教室电脑被直接断电的概率不低
        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _flushTimer.Tick += (_, _) => SleepLedger.Instance.FlushIfDirty();
        _flushTimer.Start();
    }

    private static void OnClass()
    {
        try
        {
            var context = LessonClock.Current;

            SleepLedger.Instance.BeginLesson(
                TimetableSync.Now(),
                SettingsStore.Load().ClassSize,
                context?.Index ?? 0,
                context?.Subject ?? "");
        }
        catch
        {
            // 记账失败不能影响上课
        }
    }

    private static void OnFinished(LessonContext context, LessonEndReason reason)
    {
        try
        {
            var end = context.End > context.Start ? context.End : TimetableSync.Now();

            var lesson = SleepLedger.Instance.EndLesson(end);

            // 一个人都没睡就别弹了，安静地翻过去
            if (lesson == null || lesson.Episodes.Count == 0) return;

            // 休眠 / 卡顿之后补结算的那一笔：数据是断的，弹出来只会吓人
            if (reason == LessonEndReason.Wake) return;

            var cfg = SummarySettingsStore.Load();
            if (!cfg.AutoPopupOnLessonEnd) return;

            var longest = lesson.Episodes.Count > 0
                ? FunFactWriter.Duration(lesson.Episodes.Max(e => e.Seconds))
                : "—";

            SchedulePopup(
                lesson.Sleepers > 0 ? $"这节课 {lesson.Sleepers} 个人睡过" : "这节课一个人都没睡",
                $"{lesson.Title} · 最长一觉 {longest}",
                cfg.PopupDelaySeconds);
        }
        catch
        {
            // 弹窗失败不能影响下课
        }
    }

    /// <summary>
    /// 按配置把弹窗往后推。0 秒直接弹，跟以前一致；设了延迟就等够时间再走同一条流程。
    /// 连着两节课只保留最后排的那一次，不会攒出两个窗。
    /// </summary>
    private static void SchedulePopup(string title, string detail, int delaySeconds)
    {
        _popupTimer?.Dispose();
        _popupTimer = null;

        if (delaySeconds <= 0)
        {
            Popup(title, detail);
            return;
        }

        _popupTimer = DispatcherTimer.RunOnce(() =>
        {
            _popupTimer = null;
            Popup(title, detail);
        }, TimeSpan.FromSeconds(Math.Clamp(delaySeconds, 1, 120)));
    }

    private static void Popup(string title, string detail)
    {
        try
        {
            // 先把 ClassIsland 的强提醒打出去：全屏遮罩 + 提示音，教室里才看得见
            StrongAlert.Show(title, detail, 8);

            // 遮罩起来之后再把自己的小窗贴上去，两份信息互补，不打架
            DispatcherTimer.RunOnce(() =>
            {
                try
                {
                    // 延迟期间可能在设置页里改过配色和显示项，弹之前现读一次
                    SummaryWindow.Show(SummarySettingsStore.Load(), sample: false);
                }
                catch
                {
                    // 弹窗失败不影响下课
                }
            }, TimeSpan.FromMilliseconds(1100));
        }
        catch
        {
            // 弹窗失败不能影响下课
        }
    }

    /// <summary>设置页里手动打开（示例数据用来调配色）。</summary>
    public static void ShowNow(bool sample)
    {
        PreviewLab.Open(sample);
    }

    /// <summary>当前有没有正在上课（给设置页显示用）。</summary>
    public static bool InClass => LessonClock.Current != null;
}
