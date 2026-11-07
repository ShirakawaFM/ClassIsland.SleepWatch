using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
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
    private Slider _popupDelaySlider = null!;
    private TextBlock _popupDelayValue = null!;
    private TextBlock _popupDelayHint = null!;
    private Button _summaryButton = null!;
    private Button _sampleSummaryButton = null!;

    private Border _phaseDot = null!;
    private TextBlock _phaseTitle = null!;
    private TextBlock _phaseDetail = null!;
    private TextBlock _phaseNext = null!;
    private TextBlock _syncStatus = null!;
    private TextBlock _clockWarning = null!;
    private TextBlock _previewStatus = null!;
    private Button _syncButton = null!;
    private Button _simulateClassButton = null!;
    private Button _simulateBreakButton = null!;
    private Button _previewLiveButton = null!;
    private Button _previewSampleButton = null!;
    private Button _previewRerollButton = null!;

    // ---- 1.5.0 新增 ----
    private ComboBox _cameraCombo = null!;
    private Button _cameraRefreshButton = null!;
    private TextBlock _cameraHint = null!;
    private TextBlock _classSizeHint = null!;

    private Slider _confirmSlider = null!;
    private Slider _postureSlider = null!;
    private Slider _occlusionSlider = null!;
    private Slider _cacheSlider = null!;
    private Slider _podiumSlider = null!;
    private TextBlock _confirmValue = null!;
    private TextBlock _postureValue = null!;
    private TextBlock _occlusionValue = null!;
    private TextBlock _cacheValue = null!;
    private TextBlock _podiumValue = null!;

    private CheckBox _legacyPostureBox = null!;
    private CheckBox _excludeTeacherBox = null!;
    private CheckBox _singleEyeBox = null!;
    private CheckBox _trustPostureBox = null!;
    private CheckBox _chunkBox = null!;
    private CheckBox _micThrottleBox = null!;

    private ComboBox _presetCombo = null!;
    private TextBlock _presetNote = null!;

    private CheckBox _snapshotBox = null!;
    private TextBox _snapshotFolderBox = null!;
    private Button _snapshotPickButton = null!;
    private Button _snapshotOpenButton = null!;
    private Slider _snapshotDaysSlider = null!;
    private Slider _snapshotQualitySlider = null!;
    private TextBlock _snapshotDaysValue = null!;
    private TextBlock _snapshotQualityValue = null!;
    private TextBlock _snapshotUsageText = null!;

    private WrapPanel _subjectPanel = null!;

    private ComboBox _logLevelCombo = null!;
    private Button _logFolderButton = null!;

    /// <summary>摄像头扫描是同步阻塞的（要逐个试开设备），跑的时候别让用户连点。</summary>
    private bool _scanning;

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _previewTimer;
    private bool _loading;

    /// <summary>延迟弹出那一项改过，落盘时要单独提示。</summary>
    private bool _delayDirty;

    public SleepWatchSettingsPage()
    {
        InitializeComponent();

        BindControls();
        FillCombos();
        LoadValues();
        WireEvents();
        FillAbout();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            UpdateStatus();
            UpdatePhase();
        };

        // 拖动滑动条会连续触发 Value 变化，攒 400ms 再落盘，避免疯狂写 settings.json
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();

            var delayOnly = _delayDirty;
            _delayDirty = false;

            // 两份配置各写各的文件，一起落盘，谁先拖都不会丢
            Commit(announce: !delayOnly);
            CommitPopupDelay(announce: delayOnly);
        };

        // 实时画面：约 3 帧/秒刷一次，跟检测帧率无关，纯粹是看效果
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _previewTimer.Tick += (_, _) => TickPreview();

        AttachedToVisualTree += (_, _) =>
        {
            LoadValues();
            _timer.Start();
            UpdateStatus();
            UpdatePhase();
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
        _popupDelaySlider = this.FindControl<Slider>("PopupDelaySlider")!;
        _popupDelayValue = this.FindControl<TextBlock>("PopupDelayValue")!;
        _popupDelayHint = this.FindControl<TextBlock>("PopupDelayHint")!;
        _summaryButton = this.FindControl<Button>("SummaryButton")!;
        _sampleSummaryButton = this.FindControl<Button>("SampleSummaryButton")!;

        _phaseDot = this.FindControl<Border>("PhaseDot")!;
        _phaseTitle = this.FindControl<TextBlock>("PhaseTitle")!;
        _phaseDetail = this.FindControl<TextBlock>("PhaseDetail")!;
        _phaseNext = this.FindControl<TextBlock>("PhaseNext")!;
        _syncStatus = this.FindControl<TextBlock>("SyncStatus")!;
        _clockWarning = this.FindControl<TextBlock>("ClockWarning")!;
        _previewStatus = this.FindControl<TextBlock>("PreviewStatus")!;
        _syncButton = this.FindControl<Button>("SyncButton")!;
        _simulateClassButton = this.FindControl<Button>("SimulateClassButton")!;
        _simulateBreakButton = this.FindControl<Button>("SimulateBreakButton")!;
        _previewLiveButton = this.FindControl<Button>("PreviewLiveButton")!;
        _previewSampleButton = this.FindControl<Button>("PreviewSampleButton")!;
        _previewRerollButton = this.FindControl<Button>("PreviewRerollButton")!;

        _cameraCombo = this.FindControl<ComboBox>("CameraCombo")!;
        _cameraRefreshButton = this.FindControl<Button>("CameraRefreshButton")!;
        _cameraHint = this.FindControl<TextBlock>("CameraHint")!;
        _classSizeHint = this.FindControl<TextBlock>("ClassSizeHint")!;

        _confirmSlider = this.FindControl<Slider>("ConfirmSlider")!;
        _postureSlider = this.FindControl<Slider>("PostureSlider")!;
        _occlusionSlider = this.FindControl<Slider>("OcclusionSlider")!;
        _cacheSlider = this.FindControl<Slider>("CacheSlider")!;
        _podiumSlider = this.FindControl<Slider>("PodiumSlider")!;
        _confirmValue = this.FindControl<TextBlock>("ConfirmValue")!;
        _postureValue = this.FindControl<TextBlock>("PostureValue")!;
        _occlusionValue = this.FindControl<TextBlock>("OcclusionValue")!;
        _cacheValue = this.FindControl<TextBlock>("CacheValue")!;
        _podiumValue = this.FindControl<TextBlock>("PodiumValue")!;

        _legacyPostureBox = this.FindControl<CheckBox>("LegacyPostureBox")!;
        _excludeTeacherBox = this.FindControl<CheckBox>("ExcludeTeacherBox")!;
        _singleEyeBox = this.FindControl<CheckBox>("SingleEyeBox")!;
        _trustPostureBox = this.FindControl<CheckBox>("TrustPostureBox")!;
        _chunkBox = this.FindControl<CheckBox>("ChunkBox")!;
        _micThrottleBox = this.FindControl<CheckBox>("MicThrottleBox")!;

        _presetCombo = this.FindControl<ComboBox>("PresetCombo")!;
        _presetNote = this.FindControl<TextBlock>("PresetNote")!;

        _snapshotBox = this.FindControl<CheckBox>("SnapshotBox")!;
        _snapshotFolderBox = this.FindControl<TextBox>("SnapshotFolderBox")!;
        _snapshotPickButton = this.FindControl<Button>("SnapshotPickButton")!;
        _snapshotOpenButton = this.FindControl<Button>("SnapshotOpenButton")!;
        _snapshotDaysSlider = this.FindControl<Slider>("SnapshotDaysSlider")!;
        _snapshotQualitySlider = this.FindControl<Slider>("SnapshotQualitySlider")!;
        _snapshotDaysValue = this.FindControl<TextBlock>("SnapshotDaysValue")!;
        _snapshotQualityValue = this.FindControl<TextBlock>("SnapshotQualityValue")!;
        _snapshotUsageText = this.FindControl<TextBlock>("SnapshotUsageText")!;

        _subjectPanel = this.FindControl<WrapPanel>("SubjectPanel")!;

        _logLevelCombo = this.FindControl<ComboBox>("LogLevelCombo")!;
        _logFolderButton = this.FindControl<Button>("LogFolderButton")!;
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

        // ---- 1.5.0 ----
        _confirmSlider.Value = Clamp(_confirmSlider, s.ConfirmSeconds);
        _postureSlider.Value = Clamp(_postureSlider, s.PostureWeight);
        _occlusionSlider.Value = Clamp(_occlusionSlider, s.OcclusionSeconds);
        _cacheSlider.Value = Clamp(_cacheSlider, s.MatchCacheSeconds);
        _podiumSlider.Value = Clamp(_podiumSlider, s.PodiumRatio * 100);

        _legacyPostureBox.IsChecked = s.LegacyPostureMode;
        _excludeTeacherBox.IsChecked = s.ExcludeTeacher;
        _singleEyeBox.IsChecked = s.SingleEyeMode;
        _trustPostureBox.IsChecked = s.TrustPostureOverEyes;
        _chunkBox.IsChecked = s.ChunkedInference;
        _micThrottleBox.IsChecked = s.MicThrottle;

        _presetCombo.SelectedIndex = Math.Clamp(s.Preset, 0, DetectionPresets.All.Count - 1);
        UpdatePresetNote();

        _snapshotBox.IsChecked = s.SnapshotEnabled;
        _snapshotFolderBox.Text = s.SnapshotFolder ?? "";
        _snapshotDaysSlider.Value = Clamp(_snapshotDaysSlider, s.SnapshotKeepDays);
        _snapshotQualitySlider.Value = Clamp(_snapshotQualitySlider, s.SnapshotQuality);

        _logLevelCombo.SelectedIndex = LogLevelIndex(s.LogLevel);

        FillSubjects(s);
        UpdateSnapshotUsage(s);

        try
        {
            var summary = SummarySettingsStore.Load();
            _silentBox.IsChecked = summary.SilentMode;
            _popupDelaySlider.Value = Clamp(_popupDelaySlider, summary.PopupDelaySeconds);
        }
        catch
        {
            _silentBox.IsChecked = false;
            _popupDelaySlider.Value = 0;
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

        _confirmValue.Text = ((int)Math.Round(_confirmSlider.Value)).ToString();
        _postureValue.Text = _postureSlider.Value.ToString("0.00");
        _occlusionValue.Text = ((int)Math.Round(_occlusionSlider.Value)).ToString();
        _cacheValue.Text = ((int)Math.Round(_cacheSlider.Value)).ToString();
        _podiumValue.Text = ((int)Math.Round(_podiumSlider.Value)).ToString();

        _snapshotDaysValue.Text = ((int)Math.Round(_snapshotDaysSlider.Value)).ToString();
        _snapshotQualityValue.Text = ((int)Math.Round(_snapshotQualitySlider.Value)).ToString();

        _classSizeHint.Text = _classSizeSlider.Value < 1 ? "人 · 不限上限" : "人";

        var delay = (int)Math.Round(_popupDelaySlider.Value);
        _popupDelayValue.Text = delay.ToString();
        _popupDelayHint.Text = delay <= 0 ? "秒。0 就是下课立刻弹" : "秒后才弹出总结窗";
    }

    /// <summary>输入框用：攒 400ms 再落盘，别每敲一个键就写一次配置。</summary>
    private void RestartSave()
    {
        if (_loading) return;

        _saveTimer.Stop();
        _saveTimer.Start();
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
        // 不用 LostFocus：Avalonia 12 把这个事件的委托类型改了，
        // 在 11 上编好的绑定拿到 12 的宿主里会 MissingMethodException。
        // TextChanged + 防抖落盘的效果一样，而且 11 / 12 都能跑。
        _sourceBox.TextChanged += (_, _) => RestartSave();

        WatchSlider(_fpsSlider);
        WatchSlider(_widthSlider);
        WatchSlider(_classSizeSlider);
        WatchSlider(_sleepSlider);
        WatchSlider(_presenceSlider);
        WatchSlider(_eyeSlider);
        WatchSlider(_confirmSlider);
        WatchSlider(_postureSlider);
        WatchSlider(_occlusionSlider);
        WatchSlider(_cacheSlider);
        WatchSlider(_podiumSlider);
        WatchSlider(_snapshotDaysSlider);
        WatchSlider(_snapshotQualitySlider);

        _autoStartBox.Click += (_, _) => Commit();
        _autoStopBox.Click += (_, _) => Commit();

        _legacyPostureBox.Click += (_, _) => Commit();
        _excludeTeacherBox.Click += (_, _) => Commit();
        _singleEyeBox.Click += (_, _) => Commit();
        _trustPostureBox.Click += (_, _) => Commit();
        _chunkBox.Click += (_, _) => Commit();
        _micThrottleBox.Click += (_, _) => Commit();

        _snapshotBox.Click += (_, _) =>
        {
            Commit();
            UpdateSnapshotUsage();
        };

        _snapshotFolderBox.TextChanged += (_, _) => RestartSave();

        _snapshotPickButton.Click += (_, _) => PickSnapshotFolder();
        _snapshotOpenButton.Click += (_, _) => OpenSnapshotFolder();

        _presetCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;

            // 套预设会改好几个阈值，套完重新灌一遍界面，免得滑动条还停在老位置
            var s = SettingsStore.Load();
            DetectionPresets.ApplyTo(s, Math.Max(0, _presetCombo.SelectedIndex));
            SettingsStore.Save(s);

            _loading = true;
            try
            {
                LoadValues();
            }
            finally
            {
                _loading = false;
            }

            _statusText.Text = $"已套用「{DetectionPresets.Get(s.Preset).Name}」机位预设，点【重启监测】生效。";
        };

        _logLevelCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;

            var level = LogLevelFromIndex(_logLevelCombo.SelectedIndex);
            AppLog.Configure(level);
            Commit();
            _statusText.Text = $"日志级别已改成「{level}」，立刻生效。";
        };

        _logFolderButton.Click += (_, _) => OpenLogFolder();

        _cameraRefreshButton.Click += (_, _) => _ = ScanCameras();

        _cameraCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;

            if (_cameraCombo.SelectedItem is CameraDevice device && device.Index >= 0)
            {
                _sourceBox.Text = device.Index.ToString();
                Commit();
                _cameraHint.Text = $"已选「{device.Name}」，视频源填成 {device.Index}。点【重启监测】生效。";
            }
        };

        _silentBox.Click += (_, _) => CommitSilent();

        // 延迟弹出存在 summary.json 里，跟检测参数不是一份文件，单独走一条保存路径
        _popupDelaySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty || _loading) return;

            SyncLabels();
            _delayDirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        };

        _syncButton.Click += (_, _) =>
        {
            LessonClock.EnsureStarted();
            TimetableSync.Refresh(force: true);
            UpdatePhase();

            _statusText.Text = TimetableSync.IsUsable
                ? "课表已同步，接下来会自动跟着上下课。"
                : "课表还不可用：" + TimetableSync.Message;
        };

        _simulateClassButton.Click += (_, _) => Simulate(inClass: true);
        _simulateBreakButton.Click += (_, _) => Simulate(inClass: false);

        _previewLiveButton.Click += (_, _) => DoPreview(PreviewLab.Open(sample: false));
        _previewSampleButton.Click += (_, _) => DoPreview(PreviewLab.Open(sample: true));
        _previewRerollButton.Click += (_, _) => DoPreview(PreviewLab.Reroll());

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

    // ---------- 1.5.0 新增的几块小逻辑 ----------

    /// <summary>下拉框的候选值只在构造时灌一次，之后只改 SelectedIndex。</summary>
    private void FillCombos()
    {
        foreach (var preset in DetectionPresets.All)
        {
            _presetCombo.Items.Add(new ComboBoxItem { Content = preset.Name });
        }

        foreach (var name in new[] { "仅错误", "警告 + 错误", "常规", "调试" })
        {
            _logLevelCombo.Items.Add(new ComboBoxItem { Content = name });
        }
    }

    /// <summary>配置里存的是名字，界面上换成下标，两边对不上就落回「常规」。</summary>
    private static int LogLevelIndex(string? name)
        => name?.Trim().ToLowerInvariant() switch
        {
            "error" => 0,
            "warn" => 1,
            "debug" => 3,
            _ => 2
        };

    private static string LogLevelFromIndex(int index) => index switch
    {
        0 => "Error",
        1 => "Warn",
        3 => "Debug",
        _ => "Info"
    };

    private void UpdatePresetNote()
    {
        var index = Math.Max(0, _presetCombo.SelectedIndex);
        _presetNote.Text = DetectionPresets.Get(index).Note;
    }

    /// <summary>
    /// 课程勾选。候选来自今天的课表（拿不到就把配置里勾过的也列出来），
    /// 一个都不勾等于全选，跟 <see cref="DetectionSettings.SubjectAllowed"/> 的判定一致。
    /// </summary>
    private void FillSubjects(DetectionSettings settings)
    {
        _subjectPanel.Children.Clear();

        var names = new List<string>();

        try
        {
            TimetableSync.Refresh();
            var today = TimetableSync.Today;
            if (today != null)
            {
                foreach (var block in today.Blocks)
                {
                    if (!block.IsClass) continue;
                    if (string.IsNullOrWhiteSpace(block.Label)) continue;
                    if (!names.Contains(block.Label)) names.Add(block.Label);
                }
            }
        }
        catch
        {
            // 课表读不到就只列配置里的
        }

        foreach (var subject in settings.EnabledSubjects ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(subject) && !names.Contains(subject)) names.Add(subject);
        }

        if (names.Count == 0)
        {
            _subjectPanel.Children.Add(new TextBlock
            {
                Text = "还没读到课表，科目名取不到。先在上面的课表同步里同步一次。",
                FontSize = 12,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var name in names)
        {
            var box = new CheckBox
            {
                Content = name,
                IsChecked = (settings.EnabledSubjects ?? new List<string>()).Contains(name),
                Margin = new Thickness(0, 0, 14, 4)
            };

            // 勾一下立刻落盘：科目列表是动态生成的，走不到通用保存路径
            box.Click += (_, _) =>
            {
                if (_loading) return;
                Commit(announce: false);
                _statusText.Text = "科目已保存，下一节课按新的来。";
            };

            _subjectPanel.Children.Add(box);
        }
    }

    /// <summary>把界面上勾到的科目收成列表。放在 Commit 里，勾一下就保存。</summary>
    private List<string> CollectSubjects()
    {
        var result = new List<string>();

        foreach (var child in _subjectPanel.Children)
        {
            if (child is CheckBox box && box.IsChecked == true && box.Content is string name)
            {
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
            }
        }

        return result;
    }

    /// <summary>快照占了多少盘。目录没建过就显示「还没开始存」。</summary>
    private void UpdateSnapshotUsage()
    {
        try
        {
            UpdateSnapshotUsage(SettingsStore.Load());
        }
        catch
        {
            _snapshotUsageText.Text = "占用：—";
        }
    }

    private void UpdateSnapshotUsage(DetectionSettings settings)
    {
        try
        {
            var usage = SnapshotService.Usage(settings);
            var root = SnapshotPaths.Root(settings);

            _snapshotUsageText.Text = usage.Files == 0
                ? $"占用：还没存过快照（{root}）"
                : $"占用：{usage.Files} 张 / {usage.Bytes / 1024.0 / 1024.0:0.0} MB（{root}）";
        }
        catch
        {
            _snapshotUsageText.Text = "占用：—";
        }
    }

    private void PickSnapshotFolder()
    {
        _ = PickSnapshotFolderAsync();
    }

    private async Task PickSnapshotFolderAsync()
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null) return;

            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选一个存放证据快照的目录",
                AllowMultiple = false
            });

            if (folders.Count == 0) return;

            var path = folders[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;

            _snapshotFolderBox.Text = path;
            Commit();
            UpdateSnapshotUsage();

            _statusText.Text = "快照目录已改到 " + path;
        }
        catch (Exception ex)
        {
            _statusText.Text = "选目录失败：" + ex.Message;
        }
    }

    private void OpenSnapshotFolder()
    {
        try
        {
            var root = SnapshotPaths.Root(SettingsStore.Load());
            Directory.CreateDirectory(root);
            OpenInExplorer(root);
        }
        catch (Exception ex)
        {
            _statusText.Text = "打不开快照目录：" + ex.Message;
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            var dir = Path.Combine(Plugin.ConfigFolder, "logs");
            Directory.CreateDirectory(dir);
            OpenInExplorer(dir);
        }
        catch (Exception ex)
        {
            _statusText.Text = "打不开日志目录：" + ex.Message;
        }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // 打不开就算了
        }
    }

    /// <summary>
    /// 扫摄像头。逐个试开会占用几百毫秒到一秒，所以扔到后台线程；
    /// 扫的过程中把按钮禁掉，避免连点开出一堆 VideoCapture。
    /// </summary>
    private async Task ScanCameras()
    {
        if (_scanning) return;

        _scanning = true;
        _cameraRefreshButton.IsEnabled = false;
        _cameraHint.Text = "正在逐个试开设备，可能要一两秒…";

        try
        {
            var source = (_sourceBox.Text ?? "0").Trim();
            var devices = await Task.Run(() => CameraEnumerator.Scan(source));

            _cameraCombo.Items.Clear();

            foreach (var device in devices)
            {
                _cameraCombo.Items.Add(device);
            }

            var current = int.TryParse(source, out var idx) ? idx : -1;
            var hit = devices.FirstOrDefault(d => d.Index == current);

            if (hit != null) _cameraCombo.SelectedItem = hit;

            _cameraHint.Text = CameraEnumerator.Describe(devices) +
                               "。挑一个会自动填进下面的视频源。若当前设备正被本插件使用，会显示“占用中”，属正常。";
        }
        catch (Exception ex)
        {
            _cameraHint.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            _scanning = false;
            _cameraRefreshButton.IsEnabled = true;
        }
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

    private void Commit() => Commit(announce: true);

    private void Commit(bool announce)
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

        // ---- 1.5.0 ----
        s.ConfirmSeconds = Math.Round(_confirmSlider.Value);
        s.PostureWeight = Math.Round(_postureSlider.Value, 2);
        s.OcclusionSeconds = Math.Round(_occlusionSlider.Value);
        s.MatchCacheSeconds = Math.Round(_cacheSlider.Value);
        s.PodiumRatio = Math.Round(_podiumSlider.Value / 100.0, 3);

        s.LegacyPostureMode = _legacyPostureBox.IsChecked == true;
        s.ExcludeTeacher = _excludeTeacherBox.IsChecked == true;
        s.SingleEyeMode = _singleEyeBox.IsChecked == true;
        s.TrustPostureOverEyes = _trustPostureBox.IsChecked == true;
        s.ChunkedInference = _chunkBox.IsChecked == true;
        s.MicThrottle = _micThrottleBox.IsChecked == true;

        s.SnapshotEnabled = _snapshotBox.IsChecked == true;
        s.SnapshotFolder = (_snapshotFolderBox.Text ?? "").Trim();
        s.SnapshotKeepDays = (int)Math.Round(_snapshotDaysSlider.Value);
        s.SnapshotQuality = (int)Math.Round(_snapshotQualitySlider.Value);

        s.LogLevel = LogLevelFromIndex(_logLevelCombo.SelectedIndex);

        s.EnabledSubjects = CollectSubjects();

        SettingsStore.Save(s);

        if (announce)
        {
            _statusText.Text = "已保存。改动需要点【重启监测】才会生效。";
        }
    }

    /// <summary>延迟弹出只写 summary.json，改完下一节课就生效，不用重启监测。</summary>
    private void CommitPopupDelay(bool announce)
    {
        if (_loading) return;

        try
        {
            var cfg = SummarySettingsStore.Load();
            cfg.PopupDelaySeconds = (int)Math.Round(_popupDelaySlider.Value);
            SummarySettingsStore.Save(cfg);

            if (announce)
            {
                _statusText.Text = cfg.PopupDelaySeconds <= 0
                    ? "已保存：下课后立刻弹出总结窗。"
                    : $"已保存：下课后 {cfg.PopupDelaySeconds} 秒弹出总结窗。";
            }
        }
        catch
        {
            // 写不进去就维持原样
        }
    }

    // ---------- 课表状态 ----------

    /// <summary>
    /// 刷新课表状态卡。每秒跟主状态一起刷，不额外开定时器。
    /// </summary>
    private void UpdatePhase()
    {
        try
        {
            var snap = LessonClock.Snapshot();

            _phaseTitle.Text = snap.Title;
            _phaseDetail.Text = snap.Detail;
            _phaseNext.Text = snap.Next;

            _phaseDot.Background = new SolidColorBrush(Color.Parse(snap.Phase switch
            {
                LessonPhase.InClass => "#78DC78",
                LessonPhase.Breaking => "#B4B45A",
                LessonPhase.BeforeSchool => "#7FA3CB",
                LessonPhase.AfterSchool => "#9E9E9E",
                _ => "#C08040"
            }));

            var native = TimetableSync.ReadNativeStateName();
            _syncStatus.Text = string.IsNullOrWhiteSpace(native)
                ? TimetableSync.Describe()
                : $"{TimetableSync.Describe()}　·　ClassIsland 自报状态 {native}";

            var warning = LessonClock.Warning;
            _clockWarning.IsVisible = !string.IsNullOrWhiteSpace(warning);
            _clockWarning.Text = warning;

            _previewStatus.Text = PreviewLab.LastNote + "\n" + PreviewLab.Describe();
        }
        catch (Exception ex)
        {
            _syncStatus.Text = "刷新课表状态出错：" + ex.Message;
        }
    }

    /// <summary>模拟上课 / 下课。用来验证"记账 → 结算 → 弹窗"这条链路，不用真等一节课。</summary>
    private void Simulate(bool inClass)
    {
        try
        {
            if (inClass)
            {
                var context = LessonClock.SimulateBegin();

                // 模拟上课也要让检测跑起来，否则记出来是一节空课
                var engine = Engine();
                var settings = SettingsStore.Load();

                if (engine != null && !engine.IsRunning) engine.Start(settings);

                _statusText.Text = engine is { IsRunning: true }
                    ? $"已模拟上课：{context.Title}。检测在跑，睡着的人会记进这一节，点【模拟下课】看结算。"
                    : $"已模拟上课：{context.Title}。但检测没起来，这节会是空的——先去点【重启监测】。";

                UpdatePhase();
                return;
            }

            var before = SleepLedger.Instance.LastFinished;
            var finished = LessonClock.SimulateEnd();

            if (finished == null)
            {
                _statusText.Text = "现在没有正在上的课，没什么可结的。先点【模拟上课】。";
                UpdatePhase();
                return;
            }

            var lesson = SleepLedger.Instance.LastFinished;

            if (lesson == null || ReferenceEquals(lesson, before))
            {
                _statusText.Text = $"已模拟下课：{finished.Title}。但这节课一条记录都没有（检测没跑，或者全班都醒着），所以不会弹窗。";
            }
            else if (lesson.Episodes.Count == 0)
            {
                _statusText.Text = $"已模拟下课：{finished.Title}。零睡眠记录，按设计不弹窗——每节课弹个「0 人睡觉」只会让人想关掉它。";
            }
            else
            {
                var cfg = SummarySettingsStore.Load();

                var when = !cfg.AutoPopupOnLessonEnd
                    ? "不自动弹（没开自动弹窗）"
                    : cfg.PopupDelaySeconds <= 0
                        ? "应当已经弹出来了"
                        : $"应当会在 {cfg.PopupDelaySeconds} 秒后弹出来";

                _statusText.Text = $"已模拟下课：{finished.Title}，记到 {lesson.Episodes.Count} 段睡眠。按配置{when}。";
            }

            UpdatePhase();
        }
        catch (Exception ex)
        {
            _statusText.Text = "模拟失败：" + ex.Message;
        }
    }

    private void DoPreview(bool ok)
    {
        _previewStatus.Text = PreviewLab.LastNote + "\n" + PreviewLab.Describe();
        _statusText.Text = ok ? "试弹已打开：改配色、勾选项都是实时生效的。" : "试弹没打开：" + PreviewLab.LastNote;
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
            $"在座 {s.Present}　清醒 {s.Awake}　疑似 {s.Doubtful}　确认沉睡 {s.Sleeping}　判断不了 {s.Unknown}" +
            $"　老师 {s.Excluded}　未观测 {s.Unobserved}" +
            $"　|　抬头率 {s.HeadUpRate * 100:0}%　{s.Fps:0.0} FPS" +
            (s.Throttled ? "　|　麦克风占用，降频中" : "");
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
            $"找到 {s.Present} 人 · 清醒 {s.Awake} · 疑似 {s.Doubtful} · 确认沉睡 {s.Sleeping} · " +
            $"判断不了 {s.Unknown} · 老师 {s.Excluded} · 超出人数被丢弃 {s.Filtered} · 未观测 {s.Unobserved} · 应到 {s.ClassSize}\n" +
            $"抬头率 {(s.HeadUpRate * 100):0}% · {s.Fps:0.0} FPS" +
            (s.Throttled ? " · 麦克风被占用，已降频" : "") +
            $" · 模型 {ModelManager.Describe()}";
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
