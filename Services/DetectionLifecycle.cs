using System;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 自动启停的统一入口。
///
/// 原来"上课自动开始 / 下课自动停止"这段逻辑在组件和设置页各写了一份，
/// 加课程勾选的时候两边都得改。收到这里之后只有一份：谁想跟着课表走，
/// 调一次 <see cref="Attach"/>，用不同的 key 订阅就行。
/// </summary>
public static class DetectionLifecycle
{
    /// <summary>
    /// 把引擎挂到课表事件上。
    /// <paramref name="key"/> 用来区分订阅者：同一个 key 再挂一次会顶掉上一次，不会重复启动。
    /// </summary>
    public static void Attach(DetectionEngine engine, string key = "engine")
    {
        LessonBridge.Subscribe(
            key,
            onClass: () => TryStart(engine),
            onBreak: () => TryStop(engine),
            onAfterSchool: () => TryStop(engine));
    }

    /// <summary>上课事件：看配置和课程勾选，该开就开。</summary>
    public static void TryStart(DetectionEngine engine)
    {
        try
        {
            if (engine.IsRunning) return;

            var cfg = SettingsStore.Load();
            if (!cfg.AutoStartOnClass) return;

            // 课程勾选：只勾了的课才自动开。手动点【重启监测】不走这里，不受限制
            if (!cfg.SubjectAllowed(LessonClock.Current?.Subject))
            {
                AppLog.Info($"跳过自动启动：{LessonClock.Current?.Subject} 不在勾选列表里");
                return;
            }

            engine.Start(cfg);
        }
        catch (Exception ex)
        {
            AppLog.Error("自动启动检测失败", ex);
        }
    }

    /// <summary>下课 / 放学：按配置停掉，摄像头句柄一起释放。</summary>
    public static void TryStop(DetectionEngine engine)
    {
        try
        {
            if (!SettingsStore.Load().AutoStopOnBreak) return;
            if (!engine.IsRunning) return;

            engine.Stop();
            AppLog.Info("下课，检测已停止并释放摄像头");
        }
        catch (Exception ex)
        {
            AppLog.Error("自动停止检测失败", ex);
        }
    }
}
