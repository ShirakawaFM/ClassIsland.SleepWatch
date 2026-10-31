using System;

using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using ClassIsland.SleepWatch.Services;

namespace ClassIsland.SleepWatch.Components;

[ComponentInfo(
    "6f2a9c31-8d54-4b7e-9a02-3c5e1b7d8f40",
    "睡意监测",
    "\uE9B0",
    "统计画面里疑似睡着的人数，按睁眼信号反推")]
public partial class SleepMonitorComponent : ComponentBase<SleepMonitorSettings>
{
    private readonly DispatcherTimer _timer;

    private Border _dot = null!;
    private TextBlock _mainText = null!;
    private TextBlock _subText = null!;

    private DetectionEngine? _engine;
    private bool _bound;

    // 静默模式开关缓存：每 5 秒重新读一次配置，别在 300ms 的刷新里反复读盘
    private bool _silent;
    private DateTime _silentCheckedAt = DateTime.MinValue;

    public SleepMonitorComponent()
    {
        InitializeComponent();

        _dot = this.FindControl<Border>("LevelDot")!;
        _mainText = this.FindControl<TextBlock>("MainText")!;
        _subText = this.FindControl<TextBlock>("SubText")!;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) => Refresh();

        Loaded += (_, _) => OnLoaded();
        Unloaded += (_, _) => _timer.Stop();
    }

    private void OnLoaded()
    {
        if (_bound)
        {
            _timer.Start();
            Refresh();
            return;
        }

        try
        {
            _engine = IAppHost.GetService<DetectionEngine>();
        }
        catch
        {
            _engine = null;
        }

        if (_engine == null)
        {
            _mainText.Text = "监测引擎不可用";
            return;
        }

        var engine = _engine;

        LessonBridge.Subscribe(
            "component",
            () =>
            {
                var cfg = SettingsStore.Load();
                if (cfg.AutoStartOnClass && !engine.IsRunning) engine.Start(cfg);
            },
            () =>
            {
                if (SettingsStore.Load().AutoStopOnBreak) engine.Stop();
            },
            () => engine.Stop());

        var current = SettingsStore.Load();
        if (current.AutoStartOnClass && !engine.IsRunning)
        {
            try
            {
                engine.Start(current);
            }
            catch
            {
                // 摄像头打不开的话界面会显示具体状态
            }
        }

        _bound = true;
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        if (_engine == null) return;

        var snapshot = _engine.Snapshot;
        SleepMonitorSettings? settings = Settings;

        var compact = settings?.Compact ?? false;
        var showNumbers = settings?.ShowNumbers ?? true;
        var showDetail = settings?.ShowDetail ?? true;

        if (SilentMode)
        {
            // 静默检测：课堂上不显示人数，只在结束时报账
            SetDot("#B0A99F");
            _mainText.Text = _engine.IsRunning ? "静默记录中" : "监测未启动";
            _subText.Text = showDetail && _engine.IsRunning ? "下课出总结" : "";
            return;
        }

        if (!_engine.IsRunning)
        {
            SetDot("#9E9E9E");
            _mainText.Text = "监测未启动";

            // 引擎停了但状态不是正常的"已停止"，多半是模型缺失或摄像头打不开，把原因透出来
            var status = _engine.Status ?? "";
            _subText.Text = status is "已停止" or "未启动" or "监测中" ? "" : status;
            return;
        }

        if (snapshot.Present <= 0)
        {
            SetDot("#607D8B");
            _mainText.Text = "等待画面";
            _subText.Text = showDetail ? "去设置页确认摄像头" : "";
            return;
        }

        SetDot(snapshot.Level switch
        {
            SleepLevel.Good => "#4CAF50",
            SleepLevel.Normal => "#FFB300",
            SleepLevel.Tired => "#E53935",
            _ => "#607D8B"
        });

        if (!showNumbers)
        {
            _mainText.Text = snapshot.Level switch
            {
                SleepLevel.Good => "状态不错",
                SleepLevel.Normal => "有点乏了",
                SleepLevel.Tired => "倒下一片",
                _ => "等待画面"
            };
        }
        else if (compact)
        {
            _mainText.Text = $"睡 {snapshot.Sleeping}";
        }
        else
        {
            _mainText.Text = snapshot.Sleeping > 0
                ? $"疑似睡着 {snapshot.Sleeping} 人"
                : "全班清醒";
        }

        _subText.Text = showDetail && !compact
            ? $"在座 {snapshot.Present}/{snapshot.ClassSize}"
            : "";
    }

    /// <summary>静默模式：把实时数字藏起来，数据照常记。</summary>
    private bool SilentMode
    {
        get
        {
            if ((DateTime.Now - _silentCheckedAt).TotalSeconds > 5)
            {
                _silentCheckedAt = DateTime.Now;

                try
                {
                    _silent = SummarySettingsStore.Load().SilentMode;
                }
                catch
                {
                    _silent = false;
                }
            }

            return _silent;
        }
    }

    private void SetDot(string hex)
    {
        try
        {
            _dot.Background = new SolidColorBrush(Color.Parse(hex));
        }
        catch
        {
            // 颜色解析失败不影响显示
        }
    }
}
