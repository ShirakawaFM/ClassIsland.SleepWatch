using System;
using System.Diagnostics;
using System.IO;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using ClassIsland.SleepWatch.Services;

namespace ClassIsland.SleepWatch.Views;

[SettingsPageInfo("classisland.sleepwatch.settings", "睡意监测")]
public partial class SleepWatchSettingsPage : SettingsPageBase
{
    private TextBox _sourceBox = null!;
    private Slider _fpsSlider = null!;
    private Slider _widthSlider = null!;
    private Slider _classSizeSlider = null!;
    private Slider _sleepSlider = null!;
    private Slider _presenceSlider = null!;
    private Slider _eyeSlider = null!;
    private TextBlock _fpsValue = null!;
    private TextBlock _widthValue = null!;
    private TextBlock _classSizeValue = null!;
    private TextBlock _sleepValue = null!;
    private TextBlock _presenceValue = null!;
    private TextBlock _eyeValue = null!;
    private CheckBox _autoStartBox = null!;
    private CheckBox _autoStopBox = null!;
    private CheckBox _previewToggle = null!;
    private Button _startButton = null!;
    private Button _stopButton = null!;
    private Button _resetButton = null!;
    private Button _folderButton = null!;
    private Button _repoButton = null!;
    private Button _licenseButton = null!;
    private TextBlock _versionText = null!;
    private TextBlock _statusText = null!;
    private Image _previewImage = null!;
    private TextBlock _previewPlaceholder = null!;
    private TextBlock _previewInfo = null!;
    private CheckBox _silentBox = null!;
    private Button _summaryButton = null!;
    private Button _sampleSummaryButton = null!;

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _previewTimer;
    private bool _loading;

    public SleepWatchSettingsPage()
    {
        InitializeComponent();

        BindControls();
        LoadValues();
        WireEvents();
        FillAbout();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateStatus();

        // 拖动滑动条会连续触发 Value 变化，攒 400ms 再落盘，避免疯狂写 settings.json
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Commit();
        };

        // 实时画面：约 3 帧/秒刷一次，跟检测帧率无关，纯粹是看效果
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _previewTimer.Tick += (_, _) => TickPreview();

        AttachedToVisualTree += (_, _) =>
        {
            LoadValues();
            _timer.Start();
            UpdateStatus();
            StartPreview();
        };

        DetachedFromVisualTree += (_, _) =>
        {
            _timer.Stop();
            _saveTimer.Stop();
            StopPreview();
        };
    }

    private void BindControls()
    {
        _sourceBox = this.FindControl<TextBox>("SourceBox")!;
        _fpsSlider = this.FindControl<Slider>("FpsSlider")!;
        _widthSlider = this.FindControl<Slider>("WidthSlider")!;
        _classSizeSlider = this.FindControl<Slider>("ClassSizeSlider")!;
        _sleepSlider = this.FindControl<Slider>("SleepSlider")!;
        _presenceSlider = this.FindControl<Slider>("PresenceSlider")!;
        _eyeSlider = this.FindControl<Slider>("EyeSlider")!;
        _fpsValue = this.FindControl<TextBlock>("FpsValue")!;
        _widthValue = this.FindControl<TextBlock>("WidthValue")!;
        _classSizeValue = this.FindControl<TextBlock>("ClassSizeValue")!;
        _sleepValue = this.FindControl<TextBlock>("SleepValue")!;
        _presenceValue = this.FindControl<TextBlock>("PresenceValue")!;
        _eyeValue = this.FindControl<TextBlock>("EyeValue")!;
        _autoStartBox = this.FindControl<CheckBox>("AutoStartBox")!;
        _autoStopBox = this.FindControl<CheckBox>("AutoStopBox")!;
        _previewToggle = this.FindControl<CheckBox>("PreviewToggle")!;
        _startButton = this.FindControl<Button>("StartButton")!;
        _stopButton = this.FindControl<Button>("StopButton")!;
        _resetButton = this.FindControl<Button>("ResetButton")!;
        _folderButton = this.FindControl<Button>("FolderButton")!;
        _repoButton = this.FindControl<Button>("RepoButton")!;
        _licenseButton = this.FindControl<Button>("LicenseButton")!;
        _versionText = this.FindControl<TextBlock>("VersionText")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _previewImage = this.FindControl<Image>("PreviewImage")!;
        _previewPlaceholder = this.FindControl<TextBlock>("PreviewPlaceholder")!;
        _previewInfo = this.FindControl<TextBlock>("PreviewInfo")!;
        _silentBox = this.FindControl<CheckBox>("SilentBox")!;
        _summaryButton = this.FindControl<Button>("SummaryButton")!;
        _sampleSummaryButton = this.FindControl<Button>("SampleSummaryButton")!;
    }

    private void LoadValues()
    {
        _loading = true;

        var s = SettingsStore.Load();

        _sourceBox.Text = s.VideoSource;
        _fpsSlider.Value = Clamp(_fpsSlider, s.ProcessFps);
        _widthSlider.Value = Clamp(_widthSlider, s.ProcessWidth);
        _classSizeSlider.Value = Clamp(_classSizeSlider, s.ClassSize);
        _sleepSlider.Value = Clamp(_sleepSlider, s.SleepSeconds);
        _presenceSlider.Value = Clamp(_presenceSlider, s.PresenceMinutes);
        _eyeSlider.Value = Clamp(_eyeSlider, s.EyeDarkRatio);
        _autoStartBox.IsChecked = s.AutoStartOnClass;
        _autoStopBox.IsChecked = s.AutoStopOnBreak;

        try
        {
            _silentBox.IsChecked = SummarySettingsStore.Load().SilentMode;
        }
        catch
        {
            _silentBox.IsChecked = false;
        }

        SyncLabels();

        _loading = false;
    }

    /// <summary>把配置里的值塞进滑动条时先夹到合法区间，防止旧配置超范围让控件报错。</summary>
    private static double Clamp(Slider slider, double value)
    {
        if (double.IsNaN(value)) return slider.Minimum;
        return Math.Clamp(value, slider.Minimum, slider.Maximum);
    }

    /// <summary>让每个滑动条右侧的数值跟着动。</summary>
    private void SyncLabels()
    {
        _fpsValue.Text = ((int)Math.Round(_fpsSlider.Value)).ToString();
        _widthValue.Text = ((int)Math.Round(_widthSlider.Value)).ToString();
        _classSizeValue.Text = ((int)Math.Round(_classSizeSlider.Value)).ToString();
        _sleepValue.Text = ((int)Math.Round(_sleepSlider.Value)).ToString();
        _presenceValue.Text = ((int)Math.Round(_presenceSlider.Value)).ToString();
        _eyeValue.Text = _eyeSlider.Value.ToString("0.00");
    }

    private void WatchSlider(Slider slider)
    {
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            if (_loading) return;

            SyncLabels();
            _saveTimer.Stop();
            _saveTimer.Start();
        };
    }

    private void WireEvents()
    {
        _sourceBox.LostFocus += (_, _) => Commit();

        WatchSlider(_fpsSlider);
        WatchSlider(_widthSlider);
        WatchSlider(_classSizeSlider);
        WatchSlider(_sleepSlider);
        WatchSlider(_presenceSlider);
        WatchSlider(_eyeSlider);

        _autoStartBox.Click += (_, _) => Commit();
        _autoStopBox.Click += (_, _) => Commit();

        _silentBox.Click += (_, _) => CommitSilent();

        _summaryButton.Click += (_, _) => SummaryHost.ShowNow(sample: false);
        _sampleSummaryButton.Click += (_, _) => SummaryHost.ShowNow(sample: true);

        _previewToggle.Click += (_, _) =>
        {
            if (_previewToggle.IsChecked == true) StartPreview();
            else StopPreview();
        };

        _startButton.Click += (_, _) => Restart();
        _stopButton.Click += (_, _) => StopEngine();
        _resetButton.Click += (_, _) => Engine()?.Reset();
        _folderButton.Click += (_, _) => OpenFolder();
        _repoButton.Click += (_, _) => OpenUrl(Plugin.RepoUrl);
        _licenseButton.Click += (_, _) => OpenUrl(Plugin.RepoUrl + "/blob/main/LICENSE");
    }

    /// <summary>版本号从程序集读，避免改版本时忘了同步界面。</summary>
    private void FillAbout()
    {
        try
        {
            var v = typeof(Plugin).Assembly.GetName().Version;
            if (v != null)
            {
                _versionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
            }
        }
        catch
        {
            // 读不到就留着 XAML 里的默认文本
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _statusText.Text = "打不开链接：" + ex.Message;
        }
    }

    private void Commit()
    {
        if (_loading) return;

        var s = SettingsStore.Load();

        s.VideoSource = string.IsNullOrWhiteSpace(_sourceBox.Text) ? "0" : _sourceBox.Text!.Trim();
        s.ProcessFps = (int)Math.Round(_fpsSlider.Value);
        s.ProcessWidth = (int)Math.Round(_widthSlider.Value);
        s.ClassSize = (int)Math.Round(_classSizeSlider.Value);
        s.SleepSeconds = Math.Round(_sleepSlider.Value);
        s.PresenceMinutes = Math.Round(_presenceSlider.Value);
        s.EyeDarkRatio = Math.Round(_eyeSlider.Value, 2);
        s.AutoStartOnClass = _autoStartBox.IsChecked == true;
        s.AutoStopOnBreak = _autoStopBox.IsChecked == true;

        SettingsStore.Save(s);

        _statusText.Text = "已保存。改动需要点【重启监测】才会生效。";
    }

    /// <summary>静默开关存在 summary.json 里，跟检测参数分开。</summary>
    private void CommitSilent()
    {
        if (_loading) return;

        try
        {
            var cfg = SummarySettingsStore.Load();
            cfg.SilentMode = _silentBox.IsChecked == true;
            SummarySettingsStore.Save(cfg);
            _statusText.Text = cfg.SilentMode ? "已开启静默检测：主界面不再显示人数。" : "已关闭静默检测。";
        }
        catch
        {
            // 写不进去就维持原样
        }
    }

    private DetectionEngine? Engine()
    {
        try
        {
            return IAppHost.GetService<DetectionEngine>();
        }
        catch
        {
            return null;
        }
    }

    // ---------- 实时画面 ----------

    private void StartPreview()
    {
        if (_previewToggle.IsChecked != true) return;

        _previewTimer.Start();
        TickPreview();
    }

    private void StopPreview()
    {
        _previewTimer.Stop();

        var engine = Engine();
        if (engine != null) engine.PreviewEnabled = false;

        ClearPreviewImage();
    }

    private void ClearPreviewImage()
    {
        var old = _previewImage.Source as Bitmap;
        _previewImage.Source = null;
        old?.Dispose();
    }

    private void TickPreview()
    {
        var engine = Engine();

        if (engine == null)
        {
            ShowPreviewPlaceholder("拿不到检测引擎，插件可能没加载完整");
            _previewInfo.Text = "—";
            return;
        }

        if (!engine.IsRunning)
        {
            ClearPreviewImage();
            ShowPreviewPlaceholder("监测未运行，先点下面的【重启监测】");
            _previewInfo.Text = "未运行 · " + engine.Status;
            return;
        }

        // 引擎只在 PreviewEnabled 时才画标注框，这里每帧都置一下，防止被别处关掉
        engine.PreviewEnabled = true;

        var png = engine.TakePreview();
        if (png is { Length: > 0 })
        {
            try
            {
                var old = _previewImage.Source as Bitmap;
                _previewImage.Source = new Bitmap(new MemoryStream(png));
                old?.Dispose();
                _previewPlaceholder.IsVisible = false;
            }
            catch
            {
                // 单帧解码失败就跳过，下一拍会重来
            }
        }
        else
        {
            ShowPreviewPlaceholder("正在等第一帧…");
        }

        var s = engine.Snapshot;
        _previewInfo.Text =
            $"在座 {s.Present}　清醒 {s.Awake}　疑似睡着 {s.Sleeping}　判断不了 {s.Unknown}　未观测 {s.Unobserved}" +
            $"　|　{s.Fps:0.0} FPS";
    }

    private void ShowPreviewPlaceholder(string text)
    {
        _previewPlaceholder.Text = text;
        _previewPlaceholder.IsVisible = true;
    }

    // ---------- 启停 ----------

    private void Restart()
    {
        Commit();

        var engine = Engine();
        if (engine == null)
        {
            _statusText.Text = "拿不到检测引擎，插件可能没加载完整。";
            return;
        }

        try
        {
            engine.Start(SettingsStore.Load());

            LessonBridge.Subscribe(
                "engine",
                () =>
                {
                    if (SettingsStore.Load().AutoStartOnClass && !engine.IsRunning)
                    {
                        engine.Start(SettingsStore.Load());
                    }
                },
                () =>
                {
                    if (SettingsStore.Load().AutoStopOnBreak) engine.Stop();
                },
                () => engine.Stop());

            _statusText.Text = "已开始：" + engine.Status;
            StartPreview();
        }
        catch (Exception ex)
        {
            _statusText.Text = "启动失败：" + ex.Message;
        }
    }

    private void StopEngine()
    {
        try
        {
            Engine()?.Stop();
            ClearPreviewImage();
            ShowPreviewPlaceholder("监测已停止");
            _statusText.Text = "已停止。";
        }
        catch (Exception ex)
        {
            _statusText.Text = "停止失败：" + ex.Message;
        }
    }

    private void UpdateStatus()
    {
        var engine = Engine();
        if (engine == null)
        {
            _statusText.Text = "引擎不可用。";
            return;
        }

        var s = engine.Snapshot;
        var head = engine.IsRunning ? engine.Status : "未运行 · " + engine.Status;

        _statusText.Text =
            $"{head}\n" +
            $"找到 {s.Present} 人 · 清醒 {s.Awake} · 疑似睡着 {s.Sleeping} · 判断不了 {s.Unknown} · 未观测 {s.Unobserved} · 应到 {s.ClassSize}\n" +
            $"{s.Fps:0.0} FPS · 模型 {ModelManager.Describe()}";
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Plugin.ConfigFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Plugin.ConfigFolder}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _statusText.Text = "打不开目录：" + ex.Message;
        }
    }
}
