using System;

using Avalonia.Controls;

using ClassIsland.Core.Abstractions.Controls;

namespace ClassIsland.SleepWatch.Components;

public partial class SleepMonitorComponentSettingsControl : ComponentBase<SleepMonitorSettings>
{
    private CheckBox _compactBox = null!;
    private CheckBox _numbersBox = null!;
    private CheckBox _detailBox = null!;
    private bool _loading;

    public SleepMonitorComponentSettingsControl()
    {
        InitializeComponent();

        _compactBox = this.FindControl<CheckBox>("CompactBox")!;
        _numbersBox = this.FindControl<CheckBox>("NumbersBox")!;
        _detailBox = this.FindControl<CheckBox>("DetailBox")!;

        Loaded += (_, _) => Load();

        _compactBox.Click += (_, _) => Apply(s => s.Compact = _compactBox.IsChecked == true);
        _numbersBox.Click += (_, _) => Apply(s => s.ShowNumbers = _numbersBox.IsChecked == true);
        _detailBox.Click += (_, _) => Apply(s => s.ShowDetail = _detailBox.IsChecked == true);
    }

    private void Load()
    {
        SleepMonitorSettings? settings = Settings;
        if (settings == null) return;

        _loading = true;
        _compactBox.IsChecked = settings.Compact;
        _numbersBox.IsChecked = settings.ShowNumbers;
        _detailBox.IsChecked = settings.ShowDetail;
        _loading = false;
    }

    private void Apply(Action<SleepMonitorSettings> apply)
    {
        if (_loading) return;

        SleepMonitorSettings? settings = Settings;
        if (settings == null) return;

        apply(settings);
    }
}
