using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

using ClassIsland.SleepWatch.Controls;
using ClassIsland.SleepWatch.Services;

namespace ClassIsland.SleepWatch.Views;

/// <summary>
/// 课后总结窗口。
///
/// 一个「小弹窗」，不抢全屏：920×640 居中，Esc 直接关。
/// 内容按页走，每页只留一个巨型数字 + 三个关键词 + 一张会长的曲线，
/// 目的是让人扫一眼就抓到重点，而不是读一整屏文字。
///
/// 页序固定：<b>上节课 → 前两天 → 总计</b>，末页停几秒自己关掉。
///
/// 动画全部挂在一条时间轴上（<see cref="FrameDriver"/>，跟显示器刷新同步）：
/// 数字从 0 滚上去、关键词依次浮起、柱子从左往右弹升、整块内容淡入。
/// 所有进度都是「已过秒数 / 目标秒数」，掉帧只会少画几帧，不会让动画变慢或跳变。
/// </summary>
public partial class SummaryWindow : Window
{
    private static SummaryWindow? _instance;

    /// <summary>入场动画总时长（秒）。</summary>
    private const double EnterSeconds = 0.72;

    /// <summary>末页最多停这么久就自动关，即使每页停留时间设得很长。</summary>
    private const double CloseDwellSeconds = 4.5;

    /// <summary>除了首屏那节课，最多再回看两天。页数一多，"看一眼"就变成"看一集"。</summary>
    private const int MaxDayPages = 3;

    /// <summary>把时间轴推到"入场已经播完"的位置，用来跳过动画。</summary>
    private const double SettledAge = 3600;

    private readonly SummarySettings _cfg;
    private readonly List<SummaryPage> _pages = new();
    private bool _sample;

    private bool _playing = true;
    private bool _building;
    private int _index;

    private FrameDriver? _frames;
    private DispatcherTimer? _advance;   // 自动翻页：卡片停着的时候只有它在走
    private readonly Stopwatch _pageClock = new();
    private double _pageAge;      // 这一页停多久了，管自动翻页
    private double _entrance;     // 入场动画走了多远，管数字/柱子/卡片
    private double _lastFrame = -1;

    private int _heroTarget;
    private int _heroShown = -1;
    private string _heroSuffix = "";
    private bool _heroCounts;

    private readonly List<Border> _chips = new();
    private readonly List<Control> _factRows = new();
    private readonly TranslateTransform _bodyShift = new();

    /// <summary>翻页圆点共用一只手指光标，别每次重建都新造一个平台光标。</summary>
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

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

        if (pages.Count > 0) _pages.AddRange(pages);
        else _pages.AddRange(BuildPages(_cfg, sample));

        Bind();

        _ui.BodyGrid.RenderTransform = _bodyShift;

        ApplyTheme();
        BuildPresets();
        SyncConfigToUi();
        RenderPage(0, animate: true);
        StartFrames();

        // 翻页只看"这一页停了多久"，用不着每帧算一遍。
        // 250ms 一拍，够把 7 秒的停留卡在肉眼看不出的误差里。
        _advance = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _advance.Tick += (_, _) => OnAdvanceTick();

        KeyDown += OnKeyDown;

        Closed += (_, _) =>
        {
            _frames?.Dispose();
            _frames = null;

            _advance?.Stop();
            _advance = null;
            _pageClock.Stop();

            if (ReferenceEquals(_instance, this)) _instance = null;
        };

        Opened += (_, _) =>
        {
            // 醒目一点：先置顶把注意力拉过来，几秒后放开，免得挡住别的窗口
            Topmost = true;
            DispatcherTimer.RunOnce(() =>
            {
                try
                {
                    Topmost = false;
                }
                catch
                {
                    // 窗口已经关了就无所谓
                }
            }, TimeSpan.FromSeconds(2.5));
        };
    }

    // ================= 打开入口 =================

    public static void Show(SummarySettings cfg, bool sample)
        => ShowPages(cfg, BuildPages(cfg, sample), sample);

    /// <summary>
    /// 用现成的页面弹窗。试弹（<see cref="PreviewLab"/>）走这条：
    /// 数据是自己造的，不经过账本，也不写历史。
    /// </summary>
    public static void ShowPages(SummarySettings cfg, List<SummaryPage> pages, bool sample)
    {
        void Open()
        {
            try
            {
                if (_instance != null)
                {
                    _instance.Replace(pages, sample);
                    _instance.Activate();
                    return;
                }

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

    /// <summary>
    /// 把当前这一页的入场动画推到指定进度（默认推到播完）。
    /// 截图/自检用：没有真实帧循环时（比如无头渲染），动画会停在第一帧。
    /// </summary>
    public void SkipEntrance(double progress = 1)
    {
        var p = Math.Clamp(progress, 0, 1);

        _entrance = p >= 1 ? SettledAge : p * EnterSeconds;
        AnimateEnter(p);

        // 柱子用线性进度、并且拖到整段入场时间上：OutCubic 会让它前半秒就长满，
        // "从零弹升"的过程基本看不见
        _ui.Chart.SetProgress(Ease.Segment(p, 0.06, 1));
    }

    /// <summary>换一批数据，窗口不关。</summary>
    private void Replace(List<SummaryPage> pages, bool sample)    {
        if (pages.Count == 0) return;

        _sample = sample;
        _pages.Clear();
        _pages.AddRange(pages);

        _index = 0;
        RenderPage(0, animate: true);
        RestartClock();
    }

    private static List<SummaryPage> BuildPages(SummarySettings cfg, bool sample)
    {
        var detection = SettingsStore.Load();

        var days = sample
            ? SampleData.BuildDays(Math.Clamp(cfg.HistoryDays, 3, 6))
            : SleepLedger.Instance.RecentDays(cfg.HistoryDays, DateTime.Now);

        var pages = days
            .Take(MaxDayPages)
            .Select((d, i) => SummaryBuilder.Build(d, days, cfg, detection.ClassSize, detection.SleepSeconds, i == 0))
            .ToList();

        pages.Add(SummaryBuilder.BuildTotal(days, cfg, detection.ClassSize));

        return pages;
    }

    // ================= 帧驱动 =================

    private void StartFrames()
    {
        if (_frames != null)
        {
            _frames.Start();
            return;
        }

        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;

            _frames = new FrameDriver(top, OnFrame);
            _frames.Start();
        }
        catch
        {
            // 拿不到 TopLevel 就退化成静态展示，不影响内容
        }
    }

    /// <summary>入场动画不需要的帧，别占着刷新率 —— 播完就停。</summary>
    private void StopFrames() => _frames?.Stop();

    private void RestartClock()
    {
        _pageAge = 0;
        _entrance = 0;
        _lastFrame = -1;
        _pageClock.Restart();

        StartFrames();
    }

    /// <summary>
    /// 入场动画的每一帧。
    ///
    /// 只跑入场这段时间（<see cref="EnterSeconds"/> 秒），数字滚到位、卡片浮起来就收工。
    /// 以前这条循环是常驻的：页面停在那儿等着自动翻页的十几秒里，它还在 60fps 空转，
    /// 一体机上这笔开销实打实。翻页改成按秒计时的定时器之后，这里不再需要常驻。
    /// </summary>
    private void OnFrame(TimeSpan elapsed)
    {
        var now = elapsed.TotalSeconds;

        if (_lastFrame < 0) _lastFrame = now;

        var delta = now - _lastFrame;
        _lastFrame = now;

        // 切窗口 / 拖窗口可能让一帧变得很长，别把那一下算进动画
        if (delta < 0 || delta > 0.5) delta = 1.0 / 60;

        _entrance += delta;

        var p = Math.Min(1, _entrance / EnterSeconds);

        AnimateEnter(p);

        // 柱子用线性进度、并且拖到整段入场时间上：OutCubic 会让它前半秒就长满，
        // "从零弹升"的过程基本看不见
        _ui.Chart.SetProgress(Ease.Segment(p, 0.06, 1));

        if (p >= 1) StopFrames();
    }

    /// <summary>自动翻页 / 末页自动关窗。翻页是"到点做事"，跟渲染帧没有关系。</summary>
    private void OnAdvanceTick()
    {
        if (!_playing || _pages.Count == 0) return;

        _pageAge = _pageClock.Elapsed.TotalSeconds;

        // 末页：停几秒自己关；其余页：时间到就翻
        if (_index == _pages.Count - 1)
        {
            if (_cfg.AutoCloseAtEnd &&
                _pageAge > Math.Min(_cfg.SlideSeconds, CloseDwellSeconds))
            {
                Close();
            }

            return;
        }

        if (_pageAge >= _cfg.SlideSeconds) NextPage();
    }

    /// <summary>入场：整块内容淡入上浮，数字滚动，关键词和记录依次浮起。</summary>
    private void AnimateEnter(double p)
    {
        var bodyIn = Ease.OutCubic(Ease.Segment(p, 0, 0.55));
        _ui.BodyGrid.Opacity = 0.15 + 0.85 * bodyIn;
        _bodyShift.Y = (1 - bodyIn) * 10;

        // 大数字：0 滚到目标
        var countIn = Ease.OutCubic(Ease.Segment(p, 0.05, 0.9));
        var shown = (int)Math.Round(_heroTarget * countIn);

        if (_heroCounts && shown != _heroShown)
        {
            _heroShown = shown;
            _ui.BigNumber.Text = shown.ToString(CultureInfo.InvariantCulture);
        }

        for (var i = 0; i < _chips.Count; i++)
        {
            var from = 0.16 + i * 0.07;
            var e = Ease.OutCubic(Ease.Segment(p, from, from + 0.42));

            _chips[i].Opacity = e;

            if (_chips[i].RenderTransform is TranslateTransform shift)
            {
                shift.Y = (1 - e) * 8;
            }
        }

        for (var i = 0; i < _factRows.Count; i++)
        {
            var from = 0.22 + i * 0.08;
            _factRows[i].Opacity = Ease.OutCubic(Ease.Segment(p, from, from + 0.4));
        }
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
        public TextBlock LessonLabel = null!;
        public TextBlock BigNumber = null!;
        public TextBlock BigUnit = null!;
        public TextBlock BigLabel = null!;
        public TextBlock HeroHint = null!;
        public TextBlock Headline = null!;
        public TextBlock Subline = null!;
        public TextBlock FooterNote = null!;
        public Border ChartCard = null!;
        public TextBlock ChartTitle = null!;
        public TextBlock PeakBadge = null!;
        public SleepCurveChart Chart = null!;
        public Border FactsHost = null!;
        public TextBlock FactsTitle = null!;
        public TextBlock FactsCount = null!;
        public StackPanel FactsPanel = null!;
        public WrapPanel StatsPanel = null!;
        public StackPanel Dots = null!;
        public TextBlock PlayHint = null!;
        public Grid BodyGrid = null!;
        public Grid RightCol = null!;
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
        public CheckBox CbAutoClose = null!;
        public CheckBox CbPixel = null!;

        public Slider SlideSlider = null!;
        public TextBlock SlideValue = null!;
        public Slider DelaySlider = null!;
        public TextBlock DelayValue = null!;
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
            LessonLabel = this.FindControl<TextBlock>("LessonLabel")!,
            BigNumber = this.FindControl<TextBlock>("BigNumber")!,
            BigUnit = this.FindControl<TextBlock>("BigUnit")!,
            BigLabel = this.FindControl<TextBlock>("BigLabel")!,
            HeroHint = this.FindControl<TextBlock>("HeroHint")!,
            Headline = this.FindControl<TextBlock>("Headline")!,
            Subline = this.FindControl<TextBlock>("Subline")!,
            FooterNote = this.FindControl<TextBlock>("FooterNote")!,
            ChartCard = this.FindControl<Border>("ChartCard")!,
            ChartTitle = this.FindControl<TextBlock>("ChartTitle")!,
            PeakBadge = this.FindControl<TextBlock>("PeakBadge")!,
            Chart = this.FindControl<SleepCurveChart>("Chart")!,
            FactsHost = this.FindControl<Border>("FactsHost")!,
            FactsTitle = this.FindControl<TextBlock>("FactsTitle")!,
            FactsCount = this.FindControl<TextBlock>("FactsCount")!,
            FactsPanel = this.FindControl<StackPanel>("FactsPanel")!,
            StatsPanel = this.FindControl<WrapPanel>("StatsPanel")!,
            Dots = this.FindControl<StackPanel>("Dots")!,
            PlayHint = this.FindControl<TextBlock>("PlayHint")!,
            BodyGrid = this.FindControl<Grid>("BodyGrid")!,
            RightCol = this.FindControl<Grid>("RightCol")!,
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
            CbAutoClose = this.FindControl<CheckBox>("CbAutoClose")!,
            CbPixel = this.FindControl<CheckBox>("CbPixel")!,

            SlideSlider = this.FindControl<Slider>("SlideSlider")!,
            SlideValue = this.FindControl<TextBlock>("SlideValue")!,
            DelaySlider = this.FindControl<Slider>("DelaySlider")!,
            DelayValue = this.FindControl<TextBlock>("DelayValue")!,
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

        // 整页的默认字色：模板生成的那些文字（按钮内容、勾选项）也会继承到这个值
        Foreground = TextBrush();

        // 配置面板里没有 x:Name 的标题、勾选项、输入框全靠这几个资源着色，
        // 换配色时替换资源本身，绑在上面的控件会自己刷新
        Resources["SwText"] = TextBrush();
        Resources["SwTextSoft"] = TextBrush(0.78);
        Resources["SwTextDim"] = TextBrush(0.55);
        Resources["SwTextFaint"] = TextBrush(0.42);
        Resources["SwAccent"] = AccentBrush();
        Resources["SwField"] = TextBrush(0.045);

        // 输入框、滚动条、勾选标记这些是模板画的，颜色来自主题变体；
        // 跟着底色亮暗切换，深色底上才不会出现"浅底白字"或者反过来
        RequestedThemeVariant = Luminance(_bg) > 0.5 ? ThemeVariant.Light : ThemeVariant.Dark;

        _ui.Flow.BackgroundColor = _bg;
        _ui.Flow.AccentColor = _accent;
        _ui.Flow.CellSize = _cfg.PixelSize;
        _ui.Flow.Animated = _cfg.PixelAnimation;

        _ui.Shell.Background = new SolidColorBrush(_surface, 0.87);
        _ui.Shell.BorderBrush = AccentBrush(0.26);
        _ui.LogoBox.Background = AccentBrush();

        _ui.HeaderTitle.Foreground = TextBrush();
        _ui.HeaderSub.Foreground = TextBrush(0.48);
        _ui.DayLabel.Foreground = TextBrush(0.5);
        _ui.LessonLabel.Foreground = AccentBrush();
        _ui.BigNumber.Foreground = TextBrush();
        _ui.BigUnit.Foreground = TextBrush();
        _ui.BigLabel.Foreground = TextBrush();
        _ui.HeroHint.Foreground = TextBrush(0.45);
        _ui.Headline.Foreground = TextBrush(0.72);
        _ui.Subline.Foreground = TextBrush(0.5);
        _ui.FooterNote.Foreground = TextBrush(0.4);

        _ui.ChartCard.Background = TextBrush(0.025);
        _ui.ChartCard.BorderBrush = TextBrush(0.12);
        _ui.ChartTitle.Foreground = TextBrush(0.75);
        _ui.PeakBadge.Foreground = TextBrush(0.7);

        _ui.FactsHost.Background = TextBrush(0.025);
        _ui.FactsHost.BorderBrush = TextBrush(0.12);
        _ui.FactsTitle.Foreground = TextBrush(0.8);
        _ui.FactsCount.Foreground = TextBrush(0.45);

        _ui.PlayHint.Foreground = TextBrush(0.42);
        _ui.ConfigPanel.Background = new SolidColorBrush(_surface);
        _ui.ConfigPanel.BorderBrush = TextBrush(0.15);

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

    /// <summary>底色是亮是暗。用来决定输入框这些模板控件该走浅色还是深色主题变体。</summary>
    private static double Luminance(Color c)
        => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    /// <summary>在给定底色上，黑字还是白字更清楚。</summary>
    private static Color ContrastOn(Color background)
        => Luminance(background) > 0.62 ? Color.Parse("#1F1C19") : Colors.White;

    // ================= 渲染一页 =================

    private void RenderPage(int index, bool animate)
    {
        if (_pages.Count == 0) return;

        _building = true;

        _index = Math.Clamp(index, 0, _pages.Count - 1);
        var page = _pages[_index];

        _ui.DayLabel.Text = page.DayLabel;
        _ui.LessonLabel.Text = page.LessonLabel;
        _ui.Headline.Text = page.Headline;
        _ui.Subline.Text = page.Subline;
        _ui.Subline.IsVisible = !string.IsNullOrWhiteSpace(page.Subline);
        _ui.FooterNote.Text = page.FooterNote;
        _ui.FooterNote.IsVisible = !string.IsNullOrWhiteSpace(page.FooterNote);

        // ---- 招牌大数字：取第一条统计 ----
        var hero = page.Stats.FirstOrDefault(s => s.Enabled);

        _heroTarget = 0;
        _heroShown = -1;
        _heroSuffix = "";

        // 能滚的数字才滚（"147 次" 滚 147，单位留在后面；"29 小时 22 分" 不滚）
        _heroCounts = hero != null && TryHeroCount(hero.Value, out _heroTarget, out _heroSuffix);

        _ui.BigLabel.Text = hero?.Label ?? "";
        _ui.HeroHint.Text = hero?.Hint ?? "";

        // 纯数字（或"数字 + 单位"）：数字撑大字，单位单独排；
        // 一整句话（"30 小时 21 分"）就整体缩到中号，不然横向直接溢出
        _ui.BigUnit.IsVisible = _heroCounts && _heroSuffix.Length > 0;
        _ui.BigUnit.Text = _heroSuffix.Trim();
        _ui.BigNumber.FontSize = _heroCounts ? 112 : 46;
        _ui.BigNumber.LineHeight = _heroCounts ? 116 : 54;
        _ui.BigNumber.Text = _heroCounts ? "0" : hero?.Value ?? "—";

        // ---- 关键词：只留三条 ----
        _ui.StatsPanel.Children.Clear();
        _chips.Clear();

        var chips = page.Stats.Where(s => s.Enabled && !ReferenceEquals(s, hero)).Take(3).ToList();

        foreach (var card in chips)
        {
            var chip = BuildChip(card);
            _ui.StatsPanel.Children.Add(chip);
            _chips.Add(chip);
        }

        // ---- 曲线：每页都有，末页画的是"每天" ----
        var showChart = _cfg.ShowCurve && page.Curve.Count > 0;
        _ui.ChartCard.IsVisible = showChart;
        _ui.ChartTitle.Text = page.CurveTitle;
        _ui.Chart.SetData(page.Curve, page.CurveStart, page.CurveEnd, _accent, _text, _surface, animate);
        _ui.Chart.SetProgress(animate ? 0 : 1);
        _ui.PeakBadge.Text = page.PeakBadge;

        // ---- 趣味记录：最多三条 ----
        _ui.FactsHost.IsVisible = _cfg.ShowFacts && page.Facts.Count > 0;
        _ui.FactsPanel.Children.Clear();
        _factRows.Clear();

        var facts = page.Facts.Take(3).ToList();
        _ui.FactsCount.Text = page.Facts.Count > 3 ? $"{page.Facts.Count} 条 · 只放 3 条" : $"{page.Facts.Count} 条";

        foreach (var fact in facts)
        {
            var row = BuildFactRow(fact);
            _ui.FactsPanel.Children.Add(row);
            _factRows.Add(row);
        }

        // 没有曲线也没有记录时，让左边独占整幅
        _ui.BodyGrid.ColumnDefinitions = showChart || _ui.FactsHost.IsVisible
            ? new ColumnDefinitions("1.06*,0.94*")
            : new ColumnDefinitions("1*");

        _ui.RightCol.IsVisible = showChart || _ui.FactsHost.IsVisible;

        BuildDots();

        // 入场动画从头开始；不播动画时把入场时间轴推到头，
        // 否则下一帧又会从 0 开始算，改个勾选都能看见内容重播一遍
        _pageAge = 0;
        _pageClock.Restart();
        _entrance = animate ? 0 : SettledAge;
        AnimateEnter(animate ? 0 : 1);

        // 要播入场才把帧循环拉起来，静态页让它继续歇着
        if (animate) StartFrames();
        else StopFrames();

        _building = false;
    }

    /// <summary>
    /// 把一个"数字 + 单位"的值拆成可滚动的部分：147 次 → (147, " 次")。
    /// 值里有第二个数字（比如 29 小时 22 分）就不滚，免得只滚了前半截。
    /// </summary>
    private static bool TryHeroCount(string value, out int number, out string suffix)
    {
        number = 0;
        suffix = "";

        var match = Regex.Match(value ?? "", @"^(\d+)(\D*)$");
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return false;

        suffix = match.Groups[2].Value.Trim();
        if (suffix.Length > 0) suffix = " " + suffix;

        return true;
    }

    /// <summary>关键词：一个小方块，上面一个词，下面一个值。字不多，但一眼能读到。</summary>
    private Border BuildChip(StatCard card)
    {
        var label = new TextBlock
        {
            Text = card.Label,
            FontSize = 10.5,
            Foreground = TextBrush(0.55)
        };

        var value = new TextBlock
        {
            Text = card.Value,
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            FontFamily = new FontFamily("Georgia, Times New Roman, 宋体"),
            Foreground = TextBrush(),
            Margin = new Thickness(0, 1, 0, 0)
        };

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(label);
        stack.Children.Add(value);

        return new Border
        {
            MinWidth = 112,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(12, 7, 12, 8),
            CornerRadius = new CornerRadius(11),
            Background = AccentBrush(0.075),
            BorderThickness = new Thickness(1),
            BorderBrush = TextBrush(0.13),
            Child = stack,
            Opacity = 0,
            RenderTransform = new TranslateTransform()
        };
    }

    /// <summary>一条记录：左边一个两字标签，右边一句话，最多两行。</summary>
    private Control BuildFactRow(FactItem fact)
    {
        var tag = new TextBlock
        {
            Text = fact.Tag,
            FontSize = 10,
            FontFamily = new FontFamily("Consolas, monospace"),
            Foreground = AccentBrush(0.9),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 9, 0)
        };

        var text = new TextBlock
        {
            Text = fact.Text,
            FontSize = 11.5,
            LineHeight = 17,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextBrush(0.82)
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(tag);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return new Border
        {
            Padding = new Thickness(9, 7, 10, 7),
            CornerRadius = new CornerRadius(10),
            Background = TextBrush(0.028),
            BorderThickness = new Thickness(1),
            BorderBrush = AccentBrush(0.13),
            Child = grid,
            Opacity = 0
        };
    }

    private void BuildDots()
    {
        _ui.Dots.Children.Clear();

        for (var i = 0; i < _pages.Count; i++)
        {
            var dot = new Border
            {
                Width = i == _index ? 18 : 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Background = i == _index ? AccentBrush() : TextBrush(0.16),
                Cursor = HandCursor
            };

            var target = i;
            dot.PointerPressed += (_, _) =>
            {
                RenderPage(target, animate: true);
                RestartClock();
            };

            _ui.Dots.Children.Add(dot);
        }

        _ui.PlayHint.Text = _playing
            ? (_index == _pages.Count - 1 ? "最后一页 · 稍后自动关闭" : $"自动翻页 · {_cfg.SlideSeconds} 秒")
            : "手动翻页";
    }

    // ================= 翻页 =================

    private void NextPage()
    {
        if (_pages.Count <= 1)
        {
            if (_cfg.AutoCloseAtEnd) Close();
            return;
        }

        if (_index >= _pages.Count - 1)
        {
            if (_cfg.AutoCloseAtEnd) Close();
            return;
        }

        RenderPage(_index + 1, animate: true);
        RestartClock();
    }

    private void PrevPage()
    {
        if (_pages.Count <= 1) return;

        RenderPage((_index - 1 + _pages.Count) % _pages.Count, animate: true);
        RestartClock();
    }

    private void StartAutoPlay()
    {
        _playing = _cfg.AutoPlay && _pages.Count > 1;

        _ui.PlayButton.Content = _playing ? "暂停" : "播放";

        // 暂停了就彻底停下来：不留一个空转的定时器
        if (_advance != null) _advance.IsEnabled = _playing;

        BuildDots();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right:
            case Key.Down:
            case Key.Space:
                NextPage();
                e.Handled = true;
                break;

            case Key.Left:
            case Key.Up:
                PrevPage();
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
                Width = 13,
                Height = 13,
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

                // 顺序有讲究：先换配色再重建预设按钮。
                // 反过来的话，按钮上的名字和描边还是按上一套文字色画的，
                // 从深色主题切回浅色主题时，那几行字就成了白底白字。
                ApplyTheme();
                SyncConfigToUi();
                BuildPresets();

                RenderPage(_index, animate: true);
                RestartClock();
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
        _ui.CbAutoClose.IsChecked = _cfg.AutoCloseAtEnd;
        _ui.CbPixel.IsChecked = _cfg.PixelAnimation;

        _ui.SlideSlider.Value = Math.Clamp(_cfg.SlideSeconds, 3, 30);
        _ui.SlideValue.Text = $"{_cfg.SlideSeconds}s";

        _ui.DelaySlider.Value = Math.Clamp(_cfg.PopupDelaySeconds, 0, 120);
        _ui.DelayValue.Text = _cfg.PopupDelaySeconds <= 0 ? "立刻" : $"{_cfg.PopupDelaySeconds}s";

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
                RenderPage(_index, animate: false);
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
        Toggle(_ui.CbAutoClose, v => _cfg.AutoCloseAtEnd = v);

        _ui.CbAutoPlay.Click += (_, _) =>
        {
            if (_building) return;
            _cfg.AutoPlay = _ui.CbAutoPlay.IsChecked == true;
            Commit();
            StartAutoPlay();
        };

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
            BuildDots();
        };

        _ui.DelaySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty || _building) return;

            // 只影响下课那次自动弹窗，当前这一页不用重画
            _cfg.PopupDelaySeconds = (int)Math.Round(_ui.DelaySlider.Value);
            _ui.DelayValue.Text = _cfg.PopupDelaySeconds <= 0 ? "立刻" : $"{_cfg.PopupDelaySeconds}s";
            Commit();
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
            // 换一版合成数据，原地替换页面，窗口不用关掉重开
            var seed = PreviewLab.NextSeed();
            var detection = SettingsStore.Load();
            var days = SampleData.BuildDays(Math.Clamp(_cfg.HistoryDays, 3, 6), seed);

            var pages = days
                .Take(MaxDayPages)
                .Select((d, i) => SummaryBuilder.Build(d, days, _cfg, detection.ClassSize, detection.SleepSeconds, i == 0))
                .ToList();

            pages.Add(SummaryBuilder.BuildTotal(days, _cfg, detection.ClassSize));

            Replace(pages, true);
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

            // 预设按钮上的色块和名字是按当前文字色画的，手改完文字色要跟着重建
            ApplyTheme();
            BuildPresets();
            RenderPage(_index, animate: false);
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

        _ui.PrevButton.Click += (_, _) => PrevPage();
        _ui.NextButton.Click += (_, _) => NextPage();

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
        StartAutoPlay();
        RestartClock();
    }
}
