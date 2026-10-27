using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ResourceStatus.Core;
using AppThemeMode = ResourceStatus.Core.ThemeMode;

namespace ResourceStatus.App;

public sealed class SettingsWindow : Window
{
    private readonly AppController _controller;
    private readonly TabControl _tabs;
    private readonly TextBlock _message;
    private readonly ComboBox _gpu = new() { Name = "GpuDeviceSelector" }, _network = new() { Name = "NetworkDeviceSelector" };
    private readonly ComboBox _theme = new() { Name = "ThemeSelector" };
    private readonly Slider _backgroundTransparency = new() { Name = "BackgroundTransparencySlider" };
    private readonly TextBlock _backgroundTransparencyValue = new() { Name = "BackgroundTransparencyValue" };
    private readonly DispatcherTimer _transparencySaveTimer;
    private readonly Dictionary<string, CheckBox> _generalChecks = [];
    private readonly Dictionary<MetricKind, UsageColorEditor> _usageColorEditors = [];
    private readonly Dictionary<WidgetMode, DiskSourceEditor> _diskSourceEditors = [];
    private bool _updating;
    private bool _transparencySavePending;
    private IReadOnlyList<DeviceOption>? _shownGpus, _shownNetworks;
    private string? _shownGpuId, _shownNetworkId;
    internal TabControl ProfileTabs => _tabs;

    public SettingsWindow(AppController controller)
    {
        _controller = controller;
        _transparencySaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _transparencySaveTimer.Tick += (_, _) => Save();
        Title = "Resource Status 설정";
        Width = 610;
        Height = 700;
        MinWidth = 520;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Background;
        Foreground = Theme.Text;
        FontFamily = Theme.FontFamily;
        FontSize = 12;
        UseLayoutRounding = true;
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "표시 구성", FontSize = 23, FontWeight = FontWeights.SemiBold });
        var description = new TextBlock { Text = "작은 위젯과 큰 위젯의 표시 항목을 각각 설정하세요.", Foreground = Theme.Muted, Margin = new Thickness(0, 8, 0, 20), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(description, 1); root.Children.Add(description);
        _tabs = new TabControl { Background = Theme.Background, BorderThickness = new Thickness(0) };
        Grid.SetRow(_tabs, 2); root.Children.Add(_tabs);
        BuildProfiles();
        _tabs.Items.Add(new TabItem { Header = "일반", Content = GeneralPanel() });
        _tabs.Items.Add(new TabItem { Name = "UsageColorsTab", Header = "색상 알림", Content = UsageColorsPanel() });
        _tabs.SelectedIndex = controller.Settings.WidgetMode == WidgetMode.Compact ? 0 : 1;
        _message = new TextBlock { Text = "변경 사항이 자동으로 저장됩니다.", Foreground = Theme.Muted, FontSize = 11, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(_message, 3); root.Children.Add(_message);
        Content = root;
        controller.SettingsChanged += OnSettingsChanged;
        controller.SnapshotUpdated += OnSnapshotUpdated;
        SourceInitialized += (_, _) => WindowPlatform.ApplyTheme(this, Theme.IsDark);
        Theme.Changed += OnThemeChanged;
        Closing += (_, _) =>
        {
            foreach (var editor in _usageColorEditors.Values)
                if (editor.HasChanges) CommitUsageColor(editor);
            if (_transparencySavePending) Save();
        };
        Closed += (_, _) =>
        {
            _transparencySaveTimer.Stop();
            controller.SettingsChanged -= OnSettingsChanged;
            controller.SnapshotUpdated -= OnSnapshotUpdated;
            Theme.Changed -= OnThemeChanged;
        };
        UpdateDevices();
    }

    private void BuildProfiles()
    {
        foreach (var mode in Enum.GetValues<WidgetMode>())
            _tabs.Items.Add(new TabItem { Header = mode == WidgetMode.Compact ? "작은 위젯" : "큰 위젯", Content = ProfilePanel(mode) });
    }

    private UIElement ProfilePanel(WidgetMode mode)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 15, 6, 0) };
        var profile = _controller.Settings.DisplayProfiles.For(mode);
        foreach (var metric in Enum.GetValues<MetricKind>())
        {
            var options = profile.Items[metric];
            var card = new StackPanel { Margin = new Thickness(12) };
            var head = new DockPanel { LastChildFill = true };
            var visible = new CheckBox { Content = "표시", IsChecked = options.Visible, VerticalAlignment = VerticalAlignment.Center, Name = "Visible" + metric };
            System.Windows.Automation.AutomationProperties.SetName(visible, DisplayCatalog.Label(metric) + " 표시");
            DockPanel.SetDock(visible, Dock.Right); head.Children.Add(visible);
            head.Children.Add(new TextBlock { Text = DisplayCatalog.Label(metric), FontSize = 14, FontWeight = FontWeights.SemiBold });
            card.Children.Add(head);
            var choices = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), IsEnabled = options.Visible };
            foreach (var element in DisplayCatalog.Elements(metric))
            {
                var choice = new CheckBox { Content = element.Label, IsChecked = options.Has(element.Id), Margin = new Thickness(0, 0, 16, 10), MinWidth = 130, Tag = element.Id };
                System.Windows.Automation.AutomationProperties.SetName(choice, DisplayCatalog.Label(metric) + " " + element.Label);
                bool restoringChoice = false;
                void ChoiceChanged(object sender, RoutedEventArgs args)
                {
                    if (restoringChoice) return;
                    if (!DisplayCatalog.TrySetElement(options, element.Id, choice.IsChecked == true))
                    {
                        restoringChoice = true;
                        choice.IsChecked = true;
                        restoringChoice = false;
                        _message.Text = "표시 요소는 하나 이상 필요합니다. 항목 전체를 숨기려면 ‘표시’를 끄세요.";
                        _message.Foreground = Theme.Warning;
                        return;
                    }
                    Save();
                }
                choice.Checked += ChoiceChanged;
                choice.Unchecked += ChoiceChanged;
                choices.Children.Add(choice);
            }
            DiskSourceEditor? diskEditor = null;
            if (metric == MetricKind.Disk)
            {
                diskEditor = new DiskSourceEditor
                {
                    Profile = profile,
                    Choices = new StackPanel { Name = "DiskSources" + mode, Margin = new Thickness(0, 8, 0, 0), IsEnabled = options.Visible }
                };
                _diskSourceEditors[mode] = diskEditor;
                card.Children.Add(new TextBlock
                {
                    Text = "표시할 디스크 · 여러 개 선택 가능", Foreground = Theme.Muted,
                    Margin = new Thickness(0, 12, 0, 0)
                });
                card.Children.Add(diskEditor.Choices);
                UpdateDiskSources(diskEditor);
            }
            void VisibilityChanged(object sender, RoutedEventArgs args)
            {
                options.Visible = visible.IsChecked == true;
                choices.IsEnabled = options.Visible;
                if (diskEditor is not null) diskEditor.Choices.IsEnabled = options.Visible;
                Save();
            }
            visible.Checked += VisibilityChanged;
            visible.Unchecked += VisibilityChanged;
            card.Children.Add(choices);
            stack.Children.Add(new Border { Background = Theme.Surface, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 0, 12), Child = card });
        }
        var restore = new Button { Content = "이 모드 기본값 복원", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 12) };
        restore.Click += (_, _) =>
        {
            if (mode == WidgetMode.Compact) _controller.Settings.DisplayProfiles.Compact = DisplayCatalog.CreateDefaults(mode);
            else _controller.Settings.DisplayProfiles.Expanded = DisplayCatalog.CreateDefaults(mode);
            ((TabItem)_tabs.Items[(int)mode]).Content = ProfilePanel(mode);
            Save();
        };
        stack.Children.Add(restore);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = stack };
    }

    private sealed class DiskSourceEditor
    {
        public required DisplayProfile Profile { get; init; }
        public required StackPanel Choices { get; init; }
        public Dictionary<string, CheckBox> Controls { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Updating { get; set; }
    }

    private void UpdateDiskSources(DiskSourceEditor editor)
    {
        var detected = _controller.Disks
            .Where(device => !string.Equals(device.Id, DiskCatalog.TotalId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(device => device.Number ?? int.MaxValue).ThenBy(device => device.Name, StringComparer.CurrentCulture)
            .ToList();
        var devices = new List<DiskDeviceOption> { DiskCatalog.TotalDevice };
        devices.AddRange(detected);
        var connected = devices.Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var selected in editor.Profile.DiskSources)
            if (!connected.Contains(selected.Id)) devices.Add(new(selected.Id, selected.Name, "", null));
        editor.Updating = true;
        try
        {
            var ids = devices.Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var removed in editor.Controls.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                editor.Choices.Children.Remove(editor.Controls[removed]);
                editor.Controls.Remove(removed);
            }
            for (int index = 0; index < devices.Count; index++)
            {
                var device = devices[index];
                if (!editor.Controls.TryGetValue(device.Id, out var choice))
                {
                    choice = new CheckBox { Tag = device.Id, Margin = new Thickness(0, 0, 0, 9), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                    choice.Content = text;
                    editor.Controls.Add(device.Id, choice);
                    void SourceChanged(object sender, RoutedEventArgs args)
                    {
                        if (editor.Updating) return;
                        // Use current device metadata: an existing ID can acquire a new disk number or drive letter.
                        var current = _controller.Disks.FirstOrDefault(item => string.Equals(item.Id, device.Id, StringComparison.OrdinalIgnoreCase)) ?? device;
                        if (!DiskCatalog.TrySetSource(editor.Profile, current, choice.IsChecked == true))
                        {
                            editor.Updating = true;
                            choice.IsChecked = true;
                            editor.Updating = false;
                            _message.Text = "표시할 디스크는 하나 이상 필요합니다. 디스크 전체를 숨기려면 ‘표시’를 끄세요.";
                            _message.Foreground = Theme.Warning;
                            return;
                        }
                        Save();
                    }
                    choice.Checked += SourceChanged;
                    choice.Unchecked += SourceChanged;
                }
                string label = connected.Contains(device.Id) ? device.Name : device.Name + " · 연결 안 됨";
                ((TextBlock)choice.Content).Text = label;
                choice.ToolTip = string.IsNullOrWhiteSpace(device.Model) ? label : label + "\n" + device.Model;
                System.Windows.Automation.AutomationProperties.SetName(choice, "표시할 디스크 " + label);
                choice.IsChecked = editor.Profile.DiskSources.Any(source => string.Equals(source.Id, device.Id, StringComparison.OrdinalIgnoreCase));
                // Stable IDs retain their control and keyboard focus across normal 1-second updates.
                if (editor.Choices.Children.IndexOf(choice) != index)
                {
                    editor.Choices.Children.Remove(choice);
                    editor.Choices.Children.Insert(index, choice);
                }
            }
            editor.Choices.IsEnabled = editor.Profile.Items[MetricKind.Disk].Visible;
        }
        finally { editor.Updating = false; }
    }

    private sealed class UsageColorEditor
    {
        public required MetricKind Metric { get; init; }
        public required CheckBox Enabled { get; init; }
        public required Grid Inputs { get; init; }
        public required TextBox Warning { get; init; }
        public required TextBox Critical { get; init; }
        public required TextBlock Validation { get; init; }
        public bool HasChanges { get; set; }
        public bool HasValidationError { get; set; }
    }

    private UIElement UsageColorsPanel()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 15, 6, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = "작은·큰 위젯에 함께 적용됩니다. 사용률 숫자·게이지·그래프와 네트워크 송수신 속도에 적용합니다.\n기본값은 주황 80%, 빨강 95%입니다.",
            Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, LineHeight = 19,
            Margin = new Thickness(2, 0, 2, 14)
        });
        foreach (var metric in UsageColorPolicy.SupportedMetrics)
        {
            string label = DisplayCatalog.Label(metric);
            var options = _controller.Settings.UsageColors[metric];
            var card = new StackPanel { Margin = new Thickness(12) };
            var head = new DockPanel { LastChildFill = true };
            var enabled = new CheckBox
            {
                Name = "UsageColorEnabled" + metric, Content = "사용", IsChecked = options.Enabled,
                VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Automation.AutomationProperties.SetName(enabled, label + " 색상 알림");
            DockPanel.SetDock(enabled, Dock.Right);
            head.Children.Add(enabled);
            head.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold });
            card.Children.Add(head);

            var inputs = new Grid { Margin = new Thickness(0, 10, 0, 0), IsEnabled = options.Enabled, Opacity = options.Enabled ? 1 : 0.45 };
            inputs.ColumnDefinitions.Add(new ColumnDefinition());
            inputs.ColumnDefinitions.Add(new ColumnDefinition());
            var warning = ThresholdInput("UsageColorWarning" + metric, label + " 주황 기준", options.WarningThreshold);
            var critical = ThresholdInput("UsageColorCritical" + metric, label + " 빨강 기준", options.CriticalThreshold);
            AddThresholdColumn(inputs, warning, "주황 기준", Theme.UsageWarning, 0);
            AddThresholdColumn(inputs, critical, "빨강 기준", Theme.UsageCritical, 1);
            card.Children.Add(inputs);
            var validation = new TextBlock
            {
                Name = "UsageColorValidation" + metric, Foreground = Theme.Muted, FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 0), LineHeight = 17
            };
            System.Windows.Automation.AutomationProperties.SetLiveSetting(validation, System.Windows.Automation.AutomationLiveSetting.Polite);
            card.Children.Add(validation);
            var editor = new UsageColorEditor
            {
                Metric = metric, Enabled = enabled, Inputs = inputs,
                Warning = warning, Critical = critical, Validation = validation
            };
            _usageColorEditors[metric] = editor;
            SetUsageColorMessage(editor);
            foreach (var input in new[] { warning, critical })
            {
                input.TextChanged += (_, _) =>
                {
                    if (_updating) return;
                    editor.HasChanges = true;
                    if (editor.HasValidationError)
                    {
                        TryReadUsageColor(editor, out _, out _, out string? error);
                        SetUsageColorMessage(editor, error);
                    }
                };
                input.LostKeyboardFocus += (_, _) => { if (!_updating && editor.HasChanges) CommitUsageColor(editor); };
                input.KeyDown += (_, args) =>
                {
                    if (args.Key != Key.Enter || _updating) return;
                    CommitUsageColor(editor);
                    args.Handled = true;
                };
            }
            void EnabledChanged(object sender, RoutedEventArgs args)
            {
                if (_updating) return;
                // Commit a valid draft together with this toggle. Invalid text stays local
                // to the editor; disabling an alert never writes invalid thresholds.
                if (editor.HasChanges) CommitUsageColor(editor, save: false);
                _controller.Settings.UsageColors[metric].Enabled = enabled.IsChecked == true;
                _updating = true;
                try
                {
                    inputs.IsEnabled = enabled.IsChecked == true;
                    inputs.Opacity = inputs.IsEnabled ? 1 : 0.45;
                }
                finally { _updating = false; }
                if (!editor.HasValidationError) SetUsageColorMessage(editor);
                Save();
            }
            enabled.Checked += EnabledChanged;
            enabled.Unchecked += EnabledChanged;
            stack.Children.Add(new Border
            {
                Background = Theme.Surface, BorderBrush = Theme.Border, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 0, 12), Child = card
            });
        }
        stack.Children.Add(new TextBlock
        {
            Text = "Enter를 누르거나 입력 칸을 벗어나면 유효한 기준값을 적용하고 저장합니다.\n색이 자주 바뀌지 않도록 사용률이 내려갈 때는 각 기준에 최대 3%p의 여유를 적용합니다.\n\n네트워크는 링크 사용률을 기준으로 판단합니다. 링크 속도나 사용률을 알 수 없으면 기본색을 유지합니다.",
            Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11,
            LineHeight = 19, Margin = new Thickness(2, 0, 2, 12)
        });
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = stack
        };

        static TextBox ThresholdInput(string name, string label, int value)
        {
            var input = new TextBox
            {
                Name = name, Text = value.ToString(CultureInfo.InvariantCulture), MaxLength = 3,
                Width = 64, MinHeight = 30, Padding = new Thickness(6, 4, 6, 4),
                TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center,
                Foreground = Theme.Text, Background = Theme.Background, BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1), CaretBrush = Theme.Text, SelectionBrush = Theme.Cpu
            };
            System.Windows.Automation.AutomationProperties.SetName(input, label);
            System.Windows.Automation.AutomationProperties.SetHelpText(input, "1~100 사이의 정수. 주황 기준은 빨강 기준보다 낮아야 합니다. Enter 또는 초점 이동으로 저장합니다.");
            return input;
        }

        static void AddThresholdColumn(Grid inputs, TextBox input, string label, Brush color, int column)
        {
            var content = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var heading = new StackPanel { Orientation = Orientation.Horizontal };
            heading.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = color,
                Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center
            });
            heading.Children.Add(new TextBlock { Text = label, Foreground = Theme.Text });
            content.Children.Add(new Label { Content = heading, Target = input, Padding = new Thickness(0, 0, 0, 6) });
            var value = new StackPanel { Orientation = Orientation.Horizontal };
            value.Children.Add(input);
            value.Children.Add(new TextBlock { Text = "% 이상", Foreground = Theme.Muted, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(value);
            Grid.SetColumn(content, column);
            inputs.Children.Add(content);
        }
    }

    private static bool TryReadUsageColor(UsageColorEditor editor, out int warning, out int critical, out string? error)
    {
        bool warningParsed = int.TryParse(editor.Warning.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out warning);
        bool criticalParsed = int.TryParse(editor.Critical.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out critical);
        error = !warningParsed || !criticalParsed ? "1~100 사이의 정수를 입력하세요. 입력한 값은 저장되지 않았습니다."
            : warning < 1 || warning > 99 || critical < 2 || critical > 100 ? "주황은 1~99%, 빨강은 2~100%로 입력하세요. 입력한 값은 저장되지 않았습니다."
            : warning >= critical ? "주황 기준은 빨강 기준보다 낮아야 합니다. 입력한 값은 저장되지 않았습니다."
            : null;
        return error is null;
    }

    private void SetUsageColorMessage(UsageColorEditor editor, string? error = null)
    {
        editor.HasValidationError = error is not null;
        editor.Validation.Text = error ?? (editor.Enabled.IsChecked == true
            ? "1~100% · 주황 기준은 빨강 기준보다 낮아야 합니다."
            : "기본색을 사용합니다. 설정한 기준값은 보존됩니다.");
        editor.Validation.Foreground = error is null ? Theme.Muted : Theme.Warning;
        editor.Warning.BorderBrush = editor.Critical.BorderBrush = error is null ? Theme.Border : Theme.Warning;
    }

    private bool CommitUsageColor(UsageColorEditor editor, bool save = true)
    {
        if (!TryReadUsageColor(editor, out int warning, out int critical, out string? error))
        {
            SetUsageColorMessage(editor, error);
            return false;
        }
        var options = _controller.Settings.UsageColors[editor.Metric];
        bool changed = options.WarningThreshold != warning || options.CriticalThreshold != critical;
        options.WarningThreshold = warning;
        options.CriticalThreshold = critical;
        editor.HasChanges = false;
        _updating = true;
        try
        {
            editor.Warning.Text = warning.ToString(CultureInfo.InvariantCulture);
            editor.Critical.Text = critical.ToString(CultureInfo.InvariantCulture);
        }
        finally { _updating = false; }
        SetUsageColorMessage(editor);
        if (save && changed) Save();
        return true;
    }

    private void SyncUsageColorEditors()
    {
        foreach (var (metric, editor) in _usageColorEditors)
        {
            var options = _controller.Settings.UsageColors[metric];
            editor.Enabled.IsChecked = options.Enabled;
            editor.Inputs.IsEnabled = options.Enabled;
            editor.Inputs.Opacity = options.Enabled ? 1 : 0.45;
            // Another setting can be saved while a threshold is only partly typed.
            // Preserve that local draft until it is committed or this window closes.
            if (editor.HasChanges) continue;
            editor.Warning.Text = options.WarningThreshold.ToString(CultureInfo.InvariantCulture);
            editor.Critical.Text = options.CriticalThreshold.ToString(CultureInfo.InvariantCulture);
            SetUsageColorMessage(editor);
        }
    }

    private UIElement GeneralPanel()
    {
        var stack = new StackPanel { Margin = new Thickness(2, 20, 6, 0) };
        var updateRow = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
        var update = new Button { Name = "SettingsUpdateButton", Content = "업데이트 확인", Padding = new Thickness(12, 7, 12, 7) };
        update.Click += (_, _) => _controller.RequestUpdate();
        DockPanel.SetDock(update, Dock.Right);
        updateRow.Children.Add(update);
        updateRow.Children.Add(new TextBlock
        {
            Name = "SettingsAppVersion", Text = "Resource Status · v" + UpdateService.CurrentVersionText,
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = "v" + UpdateService.CurrentVersionText,
            VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Muted, Margin = new Thickness(0, 0, 12, 0)
        });
        stack.Children.Add(updateRow);
        AddCheck("프리릴리즈 업데이트 포함", _controller.Settings.IncludePrereleaseUpdates,
            value => _controller.Settings.IncludePrereleaseUpdates = value);
        AddHelp("켜면 베타·RC 등 시험 버전도 확인합니다. 끄면 정식 버전만 확인하며, 이전 버전으로 되돌리지는 않습니다.");
        var themeRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        themeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        themeRow.ColumnDefinitions.Add(new ColumnDefinition());
        themeRow.Children.Add(new Label { Content = "테마", Target = _theme, Foreground = Theme.Text, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
        _theme.MinHeight = 32;
        _theme.SelectedValuePath = nameof(ComboBoxItem.Tag);
        _theme.Items.Add(new ComboBoxItem { Content = "검은 테마", Tag = AppThemeMode.Dark });
        _theme.Items.Add(new ComboBoxItem { Content = "하얀 테마", Tag = AppThemeMode.Light });
        _theme.Items.Add(new ComboBoxItem { Content = "Windows 시스템", Tag = AppThemeMode.System });
        _theme.SelectedValue = _controller.Settings.Theme;
        System.Windows.Automation.AutomationProperties.SetName(_theme, "테마");
        _theme.SelectionChanged += (_, _) =>
        {
            if (_updating || _theme.SelectedValue is not AppThemeMode mode || !Enum.IsDefined(mode)) return;
            if (_controller.Settings.Theme == mode) return;
            _controller.Settings.Theme = mode;
            Save();
        };
        Grid.SetColumn(_theme, 1);
        themeRow.Children.Add(_theme);
        stack.Children.Add(themeRow);
        stack.Children.Add(new TextBlock
        {
            Text = "Windows 시스템은 Windows의 앱 색 모드 설정을 따릅니다.",
            Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 24)
        });
        AddCheck("항상 위에 표시", _controller.Settings.AlwaysOnTop, value => _controller.Settings.AlwaysOnTop = value);
        AddCheck("위젯 크기 고정", _controller.Settings.LockWidgetSize, value => _controller.Settings.LockWidgetSize = value);
        AddHelp("현재 높이를 크기 모드별로 유지합니다. 내용이 넘치면 스크롤하며, 해제하면 높이를 자동으로 조절합니다.");
        AddCheck("클릭 통과", _controller.Settings.ClickThrough, value => _controller.Settings.ClickThrough = value);
        AddHelp("위젯 위에서 클릭·휠·드래그하면 뒤의 창으로 전달됩니다. 해제하려면 작업 표시줄의 트레이 아이콘을 우클릭하여 ‘클릭 통과’를 끄세요.");
        AddCheck("반투명 배경", _controller.Settings.Transparency, value => _controller.Settings.Transparency = value);
        var transparencyPanel = new StackPanel { Margin = new Thickness(22, -4, 0, 20) };
        var transparencyLabel = new DockPanel();
        _backgroundTransparencyValue.Foreground = Theme.Text;
        _backgroundTransparencyValue.MinWidth = 42;
        _backgroundTransparencyValue.TextAlignment = TextAlignment.Right;
        _backgroundTransparencyValue.Text = $"{_controller.Settings.BackgroundTransparency}%";
        DockPanel.SetDock(_backgroundTransparencyValue, Dock.Right);
        transparencyLabel.Children.Add(_backgroundTransparencyValue);
        transparencyLabel.Children.Add(new Label { Content = "배경 투명도", Target = _backgroundTransparency, Padding = new Thickness(0), Foreground = Theme.Text });
        transparencyPanel.Children.Add(transparencyLabel);
        _backgroundTransparency.Minimum = 0;
        _backgroundTransparency.Maximum = 100;
        _backgroundTransparency.TickFrequency = 1;
        _backgroundTransparency.IsSnapToTickEnabled = true;
        _backgroundTransparency.SmallChange = 1;
        _backgroundTransparency.LargeChange = 10;
        _backgroundTransparency.Value = _controller.Settings.BackgroundTransparency;
        _backgroundTransparency.IsEnabled = _controller.Settings.Transparency;
        _backgroundTransparency.Foreground = Theme.Text;
        _backgroundTransparency.Background = Theme.Surface;
        _backgroundTransparency.Margin = new Thickness(0, 8, 0, 4);
        _backgroundTransparency.MinHeight = 26;
        _backgroundTransparency.ToolTip = "0%는 불투명, 100%는 투명한 배경입니다.";
        System.Windows.Automation.AutomationProperties.SetName(_backgroundTransparency, "배경 투명도");
        System.Windows.Automation.AutomationProperties.SetHelpText(_backgroundTransparency, "0% 불투명, 100% 투명. 배경에만 적용됩니다.");
        _backgroundTransparency.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            int value = Math.Clamp((int)Math.Round(_backgroundTransparency.Value), 0, 100);
            if (_controller.Settings.BackgroundTransparency == value) return;
            _controller.Settings.BackgroundTransparency = value;
            _backgroundTransparencyValue.Text = $"{value}%";
            _controller.PreviewPreferences();
            _transparencySavePending = true;
            _transparencySaveTimer.Stop();
            _transparencySaveTimer.Start();
            _message.Text = "투명도를 적용했습니다. 변경 사항을 자동 저장합니다.";
            _message.Foreground = Theme.Muted;
        };
        transparencyPanel.Children.Add(_backgroundTransparency);
        var transparencyEndpoints = new DockPanel { LastChildFill = true };
        var transparentLabel = new TextBlock { Text = "100% 투명", Foreground = Theme.Muted, FontSize = 11 };
        DockPanel.SetDock(transparentLabel, Dock.Right);
        transparencyEndpoints.Children.Add(transparentLabel);
        transparencyEndpoints.Children.Add(new TextBlock { Text = "0% 불투명", Foreground = Theme.Muted, FontSize = 11 });
        transparencyPanel.Children.Add(transparencyEndpoints);
        transparencyPanel.Children.Add(new TextBlock
        {
            Text = "배경에만 적용됩니다. 글자와 그래프는 선명하게 유지됩니다.",
            Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 9, 0, 0)
        });
        stack.Children.Add(transparencyPanel);
        AddCheck("Windows 로그인 시 시작", _controller.Settings.StartWithWindows, value =>
        {
            if (!_controller.IsDemo) StartupRegistration.SetEnabled(value);
            _controller.Settings.StartWithWindows = value;
        });
        stack.Children.Add(new TextBlock { Text = "측정 장치", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 24, 0, 14) });
        AddDevice("GPU", _gpu);
        AddDevice("네트워크", _network);
        _gpu.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            _controller.Settings.SelectedGpuId = (_gpu.SelectedItem as DeviceOption)?.Id is { Length: > 0 } id ? id : null;
            Save();
        };
        _network.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            _controller.Settings.SelectedNetworkId = (_network.SelectedItem as DeviceOption)?.Id is { Length: > 0 } id ? id : null;
            Save();
        };
        stack.Children.Add(new TextBlock
        {
            Text = "측정 장치는 두 크기가 공유합니다. 링크 사용률은 선택한 어댑터의 링크 속도 대비 값입니다.\n\n숫자와 그래프는 1초마다 갱신하며 최근 60초를 보관합니다. 위젯을 트레이로 숨기면 측정을 일시 중지합니다.",
            Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), LineHeight = 20
        });
        if (_controller.IsDemo)
            stack.Children.Add(new TextBlock { Text = "데모 모드 · 자동 시작 등록을 변경하지 않습니다.", Foreground = Theme.Warning, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) });
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };

        void AddHelp(string text) => stack.Children.Add(new TextBlock
        {
            Text = text, Foreground = Theme.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, -7, 0, 20), LineHeight = 18
        });

        void AddCheck(string title, bool enabled, Action<bool> change)
        {
            var check = new CheckBox { Content = title, IsChecked = enabled, Margin = new Thickness(0, 0, 0, 16) };
            _generalChecks[title] = check;
            void GeneralChanged(object sender, RoutedEventArgs args)
            {
                if (_updating) return;
                try { change(check.IsChecked == true); Save(); }
                catch (Exception ex)
                {
                    _updating = true;
                    check.IsChecked = !(check.IsChecked == true);
                    _updating = false;
                    _message.Text = "설정을 적용하지 못했습니다: " + ex.Message;
                    _message.Foreground = Theme.Warning;
                }
            }
            check.Checked += GeneralChanged;
            check.Unchecked += GeneralChanged;
            stack.Children.Add(check);
        }
        void AddDevice(string title, ComboBox combo)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
            combo.DisplayMemberPath = nameof(DeviceOption.Name);
            combo.MinHeight = 32;
            System.Windows.Automation.AutomationProperties.SetName(combo, title + " 측정 장치");
            Grid.SetColumn(combo, 1); row.Children.Add(combo); stack.Children.Add(row);
        }
    }

    private void Save()
    {
        _transparencySaveTimer.Stop();
        _transparencySavePending = false;
        _controller.Save();
        _message.Text = _controller.SaveError ?? "변경 사항이 저장되었습니다.";
        _message.Foreground = _controller.SaveError is null ? Theme.Muted : Theme.Warning;
    }

    // Snapshots only re-populate device selectors when the detected device lists change.
    private void OnSnapshotUpdated(object? sender, EventArgs e) => UpdateDevices();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _updating = true;
        try
        {
            _theme.SelectedValue = _controller.Settings.Theme;
            _generalChecks["항상 위에 표시"].IsChecked = _controller.Settings.AlwaysOnTop;
            _generalChecks["위젯 크기 고정"].IsChecked = _controller.Settings.LockWidgetSize;
            _generalChecks["클릭 통과"].IsChecked = _controller.Settings.ClickThrough;
            _generalChecks["프리릴리즈 업데이트 포함"].IsChecked = _controller.Settings.IncludePrereleaseUpdates;
            _generalChecks["반투명 배경"].IsChecked = _controller.Settings.Transparency;
            _backgroundTransparency.IsEnabled = _controller.Settings.Transparency;
            _backgroundTransparency.Value = _controller.Settings.BackgroundTransparency;
            _backgroundTransparencyValue.Text = $"{_controller.Settings.BackgroundTransparency}%";
            _generalChecks["Windows 로그인 시 시작"].IsChecked = _controller.Settings.StartWithWindows;
            SyncUsageColorEditors();
        }
        finally { _updating = false; }
        UpdateDevices();
        if (_controller.SaveError is not null) { _message.Text = _controller.SaveError; _message.Foreground = Theme.Warning; }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => WindowPlatform.ApplyTheme(this, Theme.IsDark);

    private void UpdateDevices()
    {
        foreach (var editor in _diskSourceEditors.Values) UpdateDiskSources(editor);
        // AppController keeps the same list instances while the detected devices are unchanged.
        if (ReferenceEquals(_shownGpus, _controller.Gpus) && ReferenceEquals(_shownNetworks, _controller.Networks)
            && _shownGpuId == _controller.Settings.SelectedGpuId && _shownNetworkId == _controller.Settings.SelectedNetworkId) return;
        _shownGpus = _controller.Gpus;
        _shownNetworks = _controller.Networks;
        _shownGpuId = _controller.Settings.SelectedGpuId;
        _shownNetworkId = _controller.Settings.SelectedNetworkId;
        _updating = true;
        try
        {
            Set(_gpu, _controller.Gpus, _controller.Settings.SelectedGpuId);
            Set(_network, _controller.Networks, _controller.Settings.SelectedNetworkId);
        }
        finally { _updating = false; }
        static void Set(ComboBox combo, IReadOnlyList<DeviceOption> devices, string? id)
        {
            var choices = new List<DeviceOption> { new("", "자동 선택") };
            choices.AddRange(devices);
            if (id is not null && !choices.Any(x => x.Id == id)) choices.Add(new(id, "연결되지 않은 장치"));
            combo.ItemsSource = choices;
            combo.SelectedItem = choices.FirstOrDefault(x => x.Id == id) ?? choices[0];
        }
    }
}
