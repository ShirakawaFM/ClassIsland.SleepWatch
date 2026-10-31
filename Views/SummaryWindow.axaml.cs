using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using ClassIsland.SleepWatch.Controls;
using ClassIsland.SleepWatch.Services;

namespace ClassIsland.SleepWatch.Views;

/// <summary>
/// 课后总结窗口。
///
/// 一页 = 一天，按天往前翻，像放幻灯片一样自己往下走。
/// 窗口是普通窗口（不是全屏、不是最大化），想关随时关。
/// </summary>
public partial class SummaryWindow : Window
{
    private static SummaryWindow? _instance;

    private readonly SummarySettings _cfg;
    private readonly List<SummaryPage> _pages;
    private readonly bool _sample;

    private readonly DispatcherTimer _slideTimer;
    private bool _playing;
    private bool _building;
    private int _index;

    private Color _bg = Color.Parse("#F7F4EC");
    private Color _surface = Color.Parse("#FFFCF6");
    private Color _accent = Color.Parse("#D97757");
    private Color _text = Color.Parse("#2E2A26");

    private BoundaryHolder _ui = new();

    public SummaryWindow() : this(SummarySettingsStore.Load(), new List<SummaryPage>(), false)
    {
    }

    public SummaryWindow(SummarySettings cfg, List<SummaryPage> pages, bool sample)
    {
        InitializeComponent();

        _cfg = cfg ?? new SummarySettings();
        _cfg.Normalize();

        _sample = sample;
        _pages = pages.Count > 0 ? pages : BuildPages(_cfg, sample);

        _slideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(_cfg.SlideSeconds, 3, 60))
        };
        _slideTimer.Tick += (_, _) => NextPage();

        Bind();
        ApplyTheme();
        BuildPresets();
        SyncConfigToUi();
        RenderPage(0);
        StartAutoPlay();

        KeyDown += OnKeyDown;
        Closed += (_, _) =>
        {
            _slideTimer.Stop();
            if (ReferenceEquals(_instance, this)) _instance = null;
        };
    }

    // ================= 打开入口 =================

    /// <summary>下课自动弹，或者从设置页手动打开。不在 UI 线程就转过去。</summary>
    public static void Show(SummarySettings cfg, bool sample)
    {
        void Open()
        {
            try
            {
                if (_instance != null)
                {
                    _instance.Activate();
                    return;
                }

                var pages = BuildPages(cfg, sample);
                var window = new SummaryWindow(cfg, pages, sample);

                _instance = window;
                window.Show();
                window.Activate();
            }
            catch
            {
                // 总结窗口出错绝不能影响上课
            }
        }

        if (Dispatcher.UIThread.CheckAccess()) Open();
        else Dispatcher.UIThread.Post(Open);
    }

    private static List<SummaryPage> BuildPages(SummarySettings cfg, bool sample)
    {
        var detection = SettingsStore.Load();

        List<DayRecord> days;

        if (sample)
        {
            days = SampleData.BuildDays(Math.Clamp(cfg.HistoryDays, 3, 6));
        }
        else
        {
            days = SleepLedger.Instance.RecentDays(cfg.HistoryDays, DateTime.Now);
        }

        return days
            .Select(d => SummaryBuilder.Build(d, days, cfg, detection.ClassSize, detection.SleepSeconds))
            .ToList();
    }

    // ================= 控件绑定 =================

    private sealed class BoundaryHolder
    {
        public PixelFlowBackground Flow = null!;
        public Border Shell = null!;
        public Border LogoBox = null!;
        public TextBlock HeaderTitle = null!;
        public TextBlock HeaderSub = null!;
        public TextBlock DayLabel = null!;
        public TextBlock Headline = null!;
        public TextBlock LessonLabel = null!;
        public TextBlock Subline = null!;
        public TextBlock FooterNote = null!;
        public Border ChartCard = null!;
        public TextBlock PeakBadge = null!;
        public SleepCurveChart Chart = null!;
        public Border FactsHost = null!;
        public TextBlock FactsTitle = null!;
        public TextBlock FactsCount = null!;
        public StackPanel FactsPanel = null!;
        public UniformGrid StatsPanel = null!;
        public StackPanel Dots = null!;
        public TextBlock PlayHint = null!;
        public Grid BodyGrid = null!;
        public Border ConfigPanel = null!;
        public WrapPanel PresetPanel = null!;

        public Button PrevButton = null!;
        public Button NextButton = null!;
        public Button PlayButton = null!;
        public Button ConfigButton = null!;
        public Button CloseButton = null!;
        public Button ConfigCloseButton = null!;
        public Button SampleButton = null!;
        public Button FolderButton = null!;

        public CheckBox CbTotal = null!;
        public CheckBox CbCurve = null!;
        public CheckBox CbRepeat = null!;
        public CheckBox CbAwakened = null!;
        public CheckBox CbLongest = null!;
        public CheckBox CbPeak = null!;
        public CheckBox CbFacts = null!;
        public CheckBox CbSilent = null!;
        public CheckBox CbAutoPopup = null!;
        public CheckBox CbAutoPlay = null!;
        public CheckBox CbPixel = null!;

        public Slider SlideSlider = null!;
        public TextBlock SlideValue = null!;
        public Slider PixelSlider = null!;
        public TextBlock PixelValue = null!;

        public TextBox HexBackground = null!;
        public TextBox HexSurface = null!;
        public TextBox HexAccent = null!;
        public TextBox HexText = null!;
    }

    private void Bind()
    {
        _ui = new BoundaryHolder
        {
            Flow = this.FindControl<PixelFlowBackground>("Flow")!,
            Shell = this.FindControl<Border>("Shell")!,
            LogoBox = this.FindControl<Border>("LogoBox")!,
            HeaderTitle = this.FindControl<TextBlock>("HeaderTitle")!,
            HeaderSub = this.FindControl<TextBlock>("HeaderSub")!,
            DayLabel = this.FindControl<TextBlock>("DayLabel")!,
            Headline = this.FindControl<TextBlock>("Headline")!,
            LessonLabel = this.FindControl<TextBlock>("LessonLabel")!,
            Subline = this.FindControl<TextBlock>("Subline")!,
            FooterNote = this.FindControl<TextBlock>("FooterNote")!,
            ChartCard = this.FindControl<Border>("ChartCard")!,
            PeakBadge = this.FindControl<TextBlock>("PeakBadge")!,
            Chart = this.FindControl<SleepCurveChart>("Chart")!,
            FactsHost = this.FindControl<Border>("FactsHost")!,
            FactsTitle = this.FindControl<TextBlock>("FactsTitle")!,
            FactsCount = this.FindControl<TextBlock>("FactsCount")!,
            FactsPanel = this.FindControl<StackPanel>("FactsPanel")!,
            StatsPanel = this.FindControl<UniformGrid>("StatsPanel")!,
            Dots = this.FindControl<StackPanel>("Dots")!,
            PlayHint = this.FindControl<TextBlock>("PlayHint")!,
            BodyGrid = this.FindControl<Grid>("BodyGrid")!,
            ConfigPanel = this.FindControl<Border>("ConfigPanel")!,
            PresetPanel = this.FindControl<WrapPanel>("PresetPanel")!,

            PrevButton = this.FindControl<Button>("PrevButton")!,
            NextButton = this.FindControl<Button>("NextButton")!,
            PlayButton = this.FindControl<Button>("PlayButton")!,
            ConfigButton = this.FindControl<Button>("ConfigButton")!,
            CloseButton = this.FindControl<Button>("CloseButton")!,
            ConfigCloseButton = this.FindControl<Button>("ConfigCloseButton")!,
            SampleButton = this.FindControl<Button>("SampleButton")!,
            FolderButton = this.FindControl<Button>("FolderButton")!,

            CbTotal = this.FindControl<CheckBox>("CbTotal")!,
            CbCurve = this.FindControl<CheckBox>("CbCurve")!,
            CbRepeat = this.FindControl<CheckBox>("CbRepeat")!,
            CbAwakened = this.FindControl<CheckBox>("CbAwakened")!,
            CbLongest = this.FindControl<CheckBox>("CbLongest")!,
            CbPeak = this.FindControl<CheckBox>("CbPeak")!,
            CbFacts = this.FindControl<CheckBox>("CbFacts")!,
            CbSilent = this.FindControl<CheckBox>("CbSilent")!,
            CbAutoPopup = this.FindControl<CheckBox>("CbAutoPopup")!,
            CbAutoPlay = this.FindControl<CheckBox>("CbAutoPlay")!,
            CbPixel = this.FindControl<CheckBox>("CbPixel")!,

            SlideSlider = this.FindControl<Slider>("SlideSlider")!,
            SlideValue = this.FindControl<TextBlock>("SlideValue")!,
            PixelSlider = this.FindControl<Slider>("PixelSlider")!,
            PixelValue = this.FindControl<TextBlock>("PixelValue")!,

            HexBackground = this.FindControl<TextBox>("HexBackground")!,
            HexSurface = this.FindControl<TextBox>("HexSurface")!,
            HexAccent = this.FindControl<TextBox>("HexAccent")!,
            HexText = this.FindControl<TextBox>("HexText")!
        };
    }

    // ================= 主题 =================

    private static Color Parse(string hex, Color fallback)
    {
        try
        {
            return Color.Parse(string.IsNullOrWhiteSpace(hex) ? "#000000" : hex.Trim());
        }
        catch
        {
            return fallback;
        }
    }

    private IBrush TextBrush(double opacity = 1) => new SolidColorBrush(_text, opacity);

    private IBrush AccentBrush(double opacity = 1) => new SolidColorBrush(_accent, opacity);

    private void ApplyTheme()
    {
        _bg = Parse(_cfg.Background, Color.Parse("#F7F4EC"));
        _surface = Parse(_cfg.Surface, Color.Parse("#FFFCF6"));
        _accent = Parse(_cfg.Accent, Color.Parse("#D97757"));
        _text = Parse(_cfg.Text, Color.Parse("#2E2A26"));

        Background = new SolidColorBrush(_bg);

        _ui.Flow.BackgroundColor = _bg;
        _ui.Flow.AccentColor = _accent;
        _ui.Flow.CellSize = _cfg.PixelSize;
        _ui.Flow.Animated = _cfg.PixelAnimation;

        _ui.Shell.Background = new SolidColorBrush(_surface, 0.90);
        _ui.Shell.BorderBrush = AccentBrush(0.26);

        _ui.LogoBox.Background = AccentBrush();

        _ui.HeaderTitle.Foreground = TextBrush();
        _ui.HeaderSub.Foreground = TextBrush(0.5);
        _ui.DayLabel.Foreground = TextBrush(0.5);
        _ui.Headline.Foreground = TextBrush();
        _ui.LessonLabel.Foreground = AccentBrush();
        _ui.Subline.Foreground = TextBrush(0.55);
        _ui.FooterNote.Foreground = TextBrush(0.4);

        _ui.ChartCard.Background = TextBrush(0.025);
        _ui.ChartCard.BorderBrush = TextBrush(0.12);
        _ui.PeakBadge.Foreground = TextBrush(0.7);

        _ui.FactsHost.Background = TextBrush(0.025);
        _ui.FactsHost.BorderBrush = TextBrush(0.12);
        _ui.FactsTitle.Foreground = TextBrush(0.8);
        _ui.FactsCount.Foreground = TextBrush(0.45);

        _ui.PlayHint.Foreground = TextBrush(0.45);

        _ui.ConfigPanel.Background = new SolidColorBrush(_surface);
        _ui.ConfigPanel.BorderBrush = TextBrush(0.15);

        // 顶栏那排按钮：跟随主题，不用系统默认的那套灰
        foreach (var button in new[]
                 {
                     _ui.PrevButton, _ui.NextButton, _ui.PlayButton, _ui.ConfigButton,
                     _ui.ConfigCloseButton, _ui.SampleButton, _ui.FolderButton
                 })
        {
            button.Foreground = TextBrush(0.85);
            button.BorderBrush = TextBrush(0.18);
        }

        _ui.CloseButton.Background = AccentBrush();
        _ui.CloseButton.Foreground = new SolidColorBrush(ContrastOn(_accent));

        Title = _sample ? "SleepWatch 课后总结 · 示例数据" : "SleepWatch 课后总结";
        _ui.HeaderSub.Text = _sample ? "示例数据 · 不写入历史" : "静默检测 · 全程无打扰";
    }

    /// <summary>在给定底色上，黑字还是白字更清楚。</summary>
    private static Color ContrastOn(Color background)
    {
        var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
        return luminance > 0.62 ? Color.Parse("#1F1C19") : Colors.White;
    }

    // ================= 渲染一页 =================

    private void RenderPage(int index)
    {
        if (_pages.Count == 0) return;

        _building = true;

        _index = Math.Clamp(index, 0, _pages.Count - 1);
        var page = _pages[_index];

        _ui.DayLabel.Text = page.DayLabel;
        _ui.Headline.Text = page.Headline;
        _ui.Headline.FontFamily = new FontFamily("Georgia, Times New Roman, 宋体");
        _ui.LessonLabel.Text = page.LessonLabel;
        _ui.Subline.Text = page.Subline;
        _ui.FooterNote.Text = page.FooterNote;
        _ui.FooterNote.IsVisible = !string.IsNullOrWhiteSpace(page.FooterNote);

        // ---- 大数字 ----
        _ui.StatsPanel.Children.Clear();
        foreach (var card in page.Stats.Where(s => s.Enabled))
        {
            _ui.StatsPanel.Children.Add(BuildStatCard(card));
        }

        // ---- 曲线 ----
        _ui.ChartCard.IsVisible = _cfg.ShowCurve;
        _ui.Chart.SetData(page.Curve, page.CurveStart, page.CurveEnd, _accent, _text, _surface);
        _ui.PeakBadge.Text = $"峰值 {page.Peak} 人";

        // ---- 趣味条目 ----
        _ui.FactsHost.IsVisible = _cfg.ShowFacts;
        _ui.FactsPanel.Children.Clear();
        _ui.FactsCount.Text = $"{page.Facts.Count} 条";

        foreach (var fact in page.Facts)
        {
            _ui.FactsPanel.Children.Add(BuildFactCard(fact));
        }

        // 右侧藏起来时，左边铺满，别留一大块空白
        _ui.BodyGrid.ColumnDefinitions = _cfg.ShowFacts
            ? new ColumnDefinitions("1.28*,0.92*")
            : new ColumnDefinitions("1*");

        BuildDots();

        _building = false;
    }

    private Control BuildStatCard(StatCard card)
    {
        var label = new TextBlock
        {
            Text = card.Label,
            FontSize = 11,
            Foreground = TextBrush(0.55)
        };

        var value = new TextBlock
        {
            Text = card.Value,
            FontSize = 21,
            FontWeight = FontWeight.Bold,
            FontFamily = new FontFamily("Georgia, Times New Roman, 宋体"),
            Foreground = TextBrush(),
            Margin = new Thickness(0, 3, 0, 2)
        };

        var hint = new TextBlock
        {
            Text = card.Hint,
            FontSize = 10,
            Foreground = TextBrush(0.42),
            TextWrapping = TextWrapping.Wrap
        };

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(label);
        stack.Children.Add(value);
        stack.Children.Add(hint);

        return new Border
        {
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(14),
            Background = AccentBrush(0.055),
            BorderThickness = new Thickness(1),
            BorderBrush = TextBrush(0.10),
            Child = stack
        };
    }

    private Control BuildFactCard(FactItem fact)
    {
        var tag = new TextBlock
        {
            Text = fact.Tag,
            FontSize = 10,
            FontFamily = new FontFamily("Consolas, monospace"),
            Foreground = AccentBrush(0.85),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 10, 0)
        };

        var text = new TextBlock
        {
            Text = fact.Text,
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextBrush(0.82)
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };

        grid.Children.Add(tag);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return new Border
        {
            Padding = new Thickness(10, 9, 12, 9),
            CornerRadius = new CornerRadius(12),
            Background = TextBrush(0.028),
            BorderThickness = new Thickness(1),
            BorderBrush = AccentBrush(0.14),
            Child = grid
        };
    }

    private void BuildDots()
    {
        _ui.Dots.Children.Clear();

        for (var i = 0; i < _pages.Count; i++)
        {
            var dot = new Border
            {
                Width = i == _index ? 20 : 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = i == _index ? AccentBrush() : TextBrush(0.16),
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            var target = i;
            dot.PointerPressed += (_, _) =>
            {
                RenderPage(target);
                RestartAutoPlay();
            };

            _ui.Dots.Children.Add(dot);
        }
    }

    // ================= 翻页 =================

    private void NextPage()
    {
        if (_pages.Count <= 1) return;

        RenderPage((_index + 1) % _pages.Count);
    }

    private void PrevPage()
    {
        if (_pages.Count <= 1) return;

        RenderPage((_index - 1 + _pages.Count) % _pages.Count);
    }

    private void StartAutoPlay()
    {
        _playing = _cfg.AutoPlay && _pages.Count > 1;

        _ui.PlayButton.Content = _playing ? "暂停" : "播放";
        _ui.PlayHint.Text = _playing ? $"自动轮播 · 每页 {_cfg.SlideSeconds} 秒" : "手动翻页";

        if (_playing) _slideTimer.Start();
        else _slideTimer.Stop();
    }

    private void RestartAutoPlay()
    {
        _slideTimer.Stop();

        if (_playing) _slideTimer.Start();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right:
            case Key.Down:
                NextPage();
                RestartAutoPlay();
                e.Handled = true;
                break;

            case Key.Left:
            case Key.Up:
                PrevPage();
                RestartAutoPlay();
                e.Handled = true;
                break;

            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    // ================= 配置面板 =================

    private void BuildPresets()
    {
        _ui.PresetPanel.Children.Clear();

        foreach (var preset in Palette.Presets)
        {
            var swatch = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Parse(preset.Accent, _accent)),
                BorderThickness = new Thickness(1),
                BorderBrush = TextBrush(0.2)
            };

            var label = new TextBlock
            {
                Text = preset.Name,
                FontSize = 11.5,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TextBrush(0.75)
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(swatch);
            content.Children.Add(label);

            var button = new Button
            {
                Content = content,
                Padding = new Thickness(9, 5, 11, 5),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                BorderBrush = TextBrush(0.15),
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 6, 6)
            };

            var captured = preset;
            button.Click += (_, _) =>
            {
                _cfg.Background = captured.Background;
                _cfg.Surface = captured.Surface;
                _cfg.Accent = captured.Accent;
                _cfg.Text = captured.Text;
                Commit();
                SyncConfigToUi();
                BuildPresets();
                ApplyTheme();
                RenderPage(_index);
            };

            _ui.PresetPanel.Children.Add(button);
        }
    }

    private void SyncConfigToUi()
    {
        _building = true;

        _ui.CbTotal.IsChecked = _cfg.ShowTotalSleepers;
        _ui.CbCurve.IsChecked = _cfg.ShowCurve;
        _ui.CbRepeat.IsChecked = _cfg.ShowRepeat;
        _ui.CbAwakened.IsChecked = _cfg.ShowAwakened;
        _ui.CbLongest.IsChecked = _cfg.ShowLongest;
        _ui.CbPeak.IsChecked = _cfg.ShowPeak;
        _ui.CbFacts.IsChecked = _cfg.ShowFacts;
        _ui.CbSilent.IsChecked = _cfg.SilentMode;
        _ui.CbAutoPopup.IsChecked = _cfg.AutoPopupOnLessonEnd;
        _ui.CbAutoPlay.IsChecked = _cfg.AutoPlay;
        _ui.CbPixel.IsChecked = _cfg.PixelAnimation;

        _ui.SlideSlider.Value = Math.Clamp(_cfg.SlideSeconds, 3, 30);
        _ui.SlideValue.Text = $"{_cfg.SlideSeconds}s";

        _ui.PixelSlider.Value = Math.Clamp(_cfg.PixelSize, 4, 16);
        _ui.PixelValue.Text = _cfg.PixelSize.ToString();

        _ui.HexBackground.Text = _cfg.Background;
        _ui.HexSurface.Text = _cfg.Surface;
        _ui.HexAccent.Text = _cfg.Accent;
        _ui.HexText.Text = _cfg.Text;

        _building = false;
    }

    private void WireConfig()
    {
        void Toggle(CheckBox box, Action<bool> apply)
        {
            box.Click += (_, _) =>
            {
                if (_building) return;
                apply(box.IsChecked == true);
                Commit();
                RenderPage(_index);
                StartAutoPlay();
            };
        }

        Toggle(_ui.CbTotal, v => _cfg.ShowTotalSleepers = v);
        Toggle(_ui.CbCurve, v => _cfg.ShowCurve = v);
        Toggle(_ui.CbRepeat, v => _cfg.ShowRepeat = v);
        Toggle(_ui.CbAwakened, v => _cfg.ShowAwakened = v);
        Toggle(_ui.CbLongest, v => _cfg.ShowLongest = v);
        Toggle(_ui.CbPeak, v => _cfg.ShowPeak = v);
        Toggle(_ui.CbFacts, v => _cfg.ShowFacts = v);
        Toggle(_ui.CbSilent, v => _cfg.SilentMode = v);
        Toggle(_ui.CbAutoPopup, v => _cfg.AutoPopupOnLessonEnd = v);
        Toggle(_ui.CbAutoPlay, v => _cfg.AutoPlay = v);

        _ui.CbPixel.Click += (_, _) =>
        {
            if (_building) return;
            _cfg.PixelAnimation = _ui.CbPixel.IsChecked == true;
            Commit();
            ApplyTheme();
        };

        _ui.SlideSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty || _building) return;

            _cfg.SlideSeconds = (int)Math.Round(_ui.SlideSlider.Value);
            _ui.SlideValue.Text = $"{_cfg.SlideSeconds}s";
            Commit();

            _slideTimer.Interval = TimeSpan.FromSeconds(_cfg.SlideSeconds);
            StartAutoPlay();
        };

        _ui.PixelSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty || _building) return;

            _cfg.PixelSize = (int)Math.Round(_ui.PixelSlider.Value);
            _ui.PixelValue.Text = _cfg.PixelSize.ToString();
            Commit();
            ApplyTheme();
        };

        WireHex(_ui.HexBackground, v => _cfg.Background = v);
        WireHex(_ui.HexSurface, v => _cfg.Surface = v);
        WireHex(_ui.HexAccent, v => _cfg.Accent = v);
        WireHex(_ui.HexText, v => _cfg.Text = v);

        _ui.SampleButton.Click += (_, _) =>
        {
            var pages = BuildPages(_cfg, true);
            var window = new SummaryWindow(_cfg, pages, true);
            window.Show();
            window.Activate();
        };

        _ui.FolderButton.Click += (_, _) =>
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
            catch
            {
                // 打不开就算了
            }
        };
    }

    private void WireHex(TextBox box, Action<string> apply)
    {
        void CommitHex()
        {
            if (_building) return;

            var value = (box.Text ?? "").Trim();

            if (!value.StartsWith('#')) value = "#" + value;

            try
            {
                _ = Color.Parse(value);
            }
            catch
            {
                // 输入到一半不是合法颜色，先别管，等输完
                return;
            }

            apply(value);
            Commit();
            ApplyTheme();
            RenderPage(_index);
        }

        box.LostFocus += (_, _) => CommitHex();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitHex();
        };
    }

    private void Commit()
    {
        _cfg.Normalize();
        SummarySettingsStore.Save(_cfg);
    }

    // ================= 静态按钮事件 =================

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // XAML 里样式是写死的，这里把事件接上
        _ui.PrevButton.Click += (_, _) =>
        {
            PrevPage();
            RestartAutoPlay();
        };

        _ui.NextButton.Click += (_, _) =>
        {
            NextPage();
            RestartAutoPlay();
        };

        _ui.PlayButton.Click += (_, _) =>
        {
            _playing = !_playing;
            _cfg.AutoPlay = _playing;
            Commit();
            StartAutoPlay();
        };

        _ui.CloseButton.Click += (_, _) => Close();

        _ui.ConfigButton.Click += (_, _) =>
        {
            _ui.ConfigPanel.IsVisible = !_ui.ConfigPanel.IsVisible;
        };

        _ui.ConfigCloseButton.Click += (_, _) => _ui.ConfigPanel.IsVisible = false;

        WireConfig();
    }
}
