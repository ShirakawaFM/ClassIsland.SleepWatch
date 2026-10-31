using System;
using System.Collections;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Shared;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 联动 ClassIsland 的强提醒。
///
/// 思路：不自己画全屏，而是借 ClassIsland 的提醒通道 —— 课后那一刻，
/// 学生收书包、老师关投影，没人会盯着我们的小弹窗。
/// 走系统的提醒才能拿到「全屏遮罩 + 声音 + 悬浮条」那一整套，
/// 我们的小窗再贴在旁边补充细节。
///
/// 接口上没有「直接弹一条提醒」的方法，只有 <see cref="INotificationHostService"/>
/// 的注册和拉取；但宿主对象上挂着 <c>NotificationProviders</c>，
/// 每个 provider 继承自 Core 的 <see cref="NotificationProviderBase"/>，
/// 它有公开的 <c>ShowNotification</c>。所以取任意一个 provider 转发即可。
///
/// 全程 try/catch：提醒发不出去是小事，绝不能因此让下课结算挂掉。
/// </summary>
public static class StrongAlert
{
    private static DateTime _lastAt = DateTime.MinValue;

    /// <summary>两次强提醒至少隔这么久，避免课表抖动导致连环弹。</summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(20);

    /// <summary>最近一次尝试的结果，设置页会显示。</summary>
    public static string LastResult { get; private set; } = "还没试过";

    /// <summary>最近一次是否真的发出去了。</summary>
    public static bool LastOk { get; private set; }

    /// <summary>
    /// 弹一条强提醒。返回是否成功交给 ClassIsland。
    /// </summary>
    public static bool Show(string title, string detail, int seconds = 8)
    {
        try
        {
            if ((DateTime.Now - _lastAt) < MinGap)
            {
                LastResult = "刚提醒过，这次跳过（防止连环弹）";
                return false;
            }

            var host = IAppHost.GetService<INotificationHostService>();
            if (host == null)
            {
                LastResult = "拿不到提醒服务";
                return false;
            }

            var request = BuildRequest(title, detail, seconds);

            var provider = FindProvider(host);
            if (provider == null)
            {
                LastResult = "没有可用的提醒通道";
                return false;
            }

            provider.ShowNotification(request);

            _lastAt = DateTime.Now;
            LastOk = true;
            LastResult = $"已通过「{provider.Name}」发出";
            return true;
        }
        catch (Exception ex)
        {
            LastOk = false;
            LastResult = "提醒发送失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>只检查能不能用，不真的发。设置页用来诊断。</summary>
    public static string Probe()
    {
        try
        {
            var host = IAppHost.GetService<INotificationHostService>();
            if (host == null) return "拿不到提醒服务";

            var provider = FindProvider(host);
            return provider == null
                ? "拿不到提醒通道（ClassIsland 版本可能不同）"
                : $"可用，走「{provider.Name}」";
        }
        catch (Exception ex)
        {
            return "检查失败：" + ex.Message;
        }
    }

    private static NotificationRequest BuildRequest(string title, string detail, int seconds)
    {
        var mask = BuildContent(title, detail, 42, 20, seconds);
        var overlay = BuildContent(title, detail, 20, 13, seconds);

        return new NotificationRequest
        {
            MaskContent = mask,
            OverlayContent = overlay
        };
    }

    private static NotificationContent BuildContent(string title, string detail, double titleSize, double detailSize, int seconds)
    {
        var stack = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = titleSize,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });

        if (!string.IsNullOrWhiteSpace(detail))
        {
            stack.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = detailSize,
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
        }

        return new NotificationContent
        {
            Content = stack,
            Duration = TimeSpan.FromSeconds(Math.Clamp(seconds, 3, 60)),
            IsSpeechEnabled = false
        };
    }

    private static NotificationProviderBase? FindProvider(object host)
    {
        // 宿主的 NotificationProviders 不在接口上，只能按名字取
        var prop = host.GetType().GetProperty("NotificationProviders",
            BindingFlags.Public | BindingFlags.Instance);

        if (prop?.GetValue(host) is not IEnumerable list) return null;

        NotificationProviderBase? fallback = null;

        foreach (var item in list)
        {
            if (item is not NotificationProviderBase provider) continue;

            // 优先用课程提醒那个通道：它天然带上下课的语义和音效
            var name = provider.Name ?? "";
            if (name.Contains("上课") || name.Contains("课程") || name.Contains("课间"))
            {
                return provider;
            }

            fallback ??= provider;
        }

        return fallback;
    }
}
