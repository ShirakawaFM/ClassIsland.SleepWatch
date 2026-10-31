namespace ClassIsland.SleepWatch.Components;

/// <summary>
/// 组件的独立设置。主界面上每放一个本组件，就有一份自己的设置。
/// </summary>
public class SleepMonitorSettings
{
    /// <summary>紧凑模式：只显示一个数字，不占地方。</summary>
    public bool Compact { get; set; }

    /// <summary>显示具体人数。关掉就只显示"状态不错/有点乏了"这种定性描述。</summary>
    public bool ShowNumbers { get; set; } = true;

    /// <summary>显示"在座/应到"的副标题。</summary>
    public bool ShowDetail { get; set; } = true;
}
