using System;
using System.IO;

using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared;
using ClassIsland.SleepWatch.Components;
using ClassIsland.SleepWatch.Services;
using ClassIsland.SleepWatch.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.SleepWatch;

/// <summary>
/// 插件入口。负责注册组件、设置页和检测引擎。
/// </summary>
[PluginEntrance]
public class Plugin : PluginBase
{
    /// <summary>插件仓库地址，设置页「关于」里会打开它。</summary>
    public const string RepoUrl = "https://github.com/ShirakawaFM/ClassIsland.SleepWatch";

    /// <summary>作者署名。</summary>
    public const string Author = "ShirakawaFM / 白川方明";

    /// <summary>
    /// 插件配置目录。检测引擎在这个目录下读写 settings.json 和 models/。
    /// 在 Initialize 时由 PluginConfigFolder 赋值，之后的代码直接读这个静态属性即可，
    /// 避免 DetectionEngine 反向依赖 Plugin 造成循环注入。
    /// </summary>
    public static string ConfigFolder { get; private set; } = "";

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        try
        {
            ConfigFolder = PluginConfigFolder;
        }
        catch
        {
            // 某些版本上这个属性可能在 Initialize 阶段还不可用，下面兜底
        }

        if (string.IsNullOrWhiteSpace(ConfigFolder))
        {
            // 兜底：绝对不能落在插件安装目录 —— 那里会在插件更新时被整个替换，
            // 配置文件会丢。文档明确要求配置放配置目录。
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            ConfigFolder = Path.Combine(appData, "ClassIsland", "SleepWatch");
        }

        try
        {
            Directory.CreateDirectory(ConfigFolder);
            Directory.CreateDirectory(Path.Combine(ConfigFolder, "models"));
        }
        catch
        {
            // 目录建不出来不影响加载，后面写配置时会再报错
        }

        services.AddSingleton<DetectionEngine>();
        services.AddSettingsPage<SleepWatchSettingsPage>();
        services.AddComponent<SleepMonitorComponent, SleepMonitorComponentSettingsControl>();
    }
}
