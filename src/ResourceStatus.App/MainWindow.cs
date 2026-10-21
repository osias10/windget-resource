using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using ResourceStatus.Core;
using ResourceStatus.App.Views;

namespace ResourceStatus.App;

public sealed class MainWindow : Window
{
    private readonly AppController _controller;
    private readonly Border _frame;
    private readonly Grid _layout;
    private readonly Grid _header;
    private readonly TextBlock _footer;
    private readonly ScrollViewer _scroll;
    private readonly CompactWidgetView _compact = new();
    private readonly ExpandedWidgetView _expanded = new();
    private readonly Button _pin;
    private WidgetMode _mode;
    private bool _loaded, _fitPending, _forceFit, _sizeMove;
    private double _fittedHeight = double.NaN;
    private readonly SolidColorBrush _frameBackground = new();
    private SettingsWindow? _settingsWindow;
    public bool AllowClose { get; set; }
    public event EventHandler? ExitRequested;
    internal FrameworkElement WidgetFrame => _frame;
    internal FrameworkElement WidgetHeader => _header;
    internal ScrollViewer WidgetScroll => _scroll;

    public MainWindow(AppController controller)
    {
        _controller = controller;
        _mode = controller.Settings.WidgetMode;
        Title = "Resource Status";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = _mode == WidgetMode.Compact ? 240 : 320;
        Height = 170;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        Foreground = Theme.Text;
        Topmost = controller.Settings.AlwaysOnTop;

        _layout = new Grid();
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition());
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _header = new Grid { Margin = new Thickness(8, 6, 8, 4) };
        _header.ColumnDefinitions.Add(new ColumnDefinition());
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.Children.Add(new TextBlock { Text = "시스템 상태", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(actions, 1);
        actions.Children.Add(ActionButton("M 11,3 L 3,11 M 3,5 L 3,11 L 9,11", "작은 위젯으로 보기", () => SetMode(WidgetMode.Compact)));
        _pin = ActionButton("M 5,2 L 9,2 M 6,2 L 6,6 L 4,9 L 10,9 L 8,6 L 8,2 M 7,9 L 7,13", "항상 위에 표시", () => { controller.Settings.AlwaysOnTop = !controller.Settings.AlwaysOnTop; controller.Save(); });
        actions.Children.Add(_pin);
        actions.Children.Add(ActionButton("M 6,1 L 8,1 L 8.5,3 L 10,3.8 L 12,3.4 L 13,5.2 L 11.5,6.6 L 11.5,8 L 13,9.2 L 12,11 L 10,10.5 L 8.5,11.3 L 8,13 L 6,13 L 5.5,11.3 L 4,10.5 L 2,11 L 1,9.2 L 2.5,8 L 2.5,6.6 L 1,5.2 L 2,3.4 L 4,3.8 L 5.5,3 Z M 9,7 A 2,2 0 1 1 5,7 A 2,2 0 1 1 9,7", "설정", ShowSettings));
        actions.Children.Add(ActionButton("M 3,3 L 11,11 M 11,3 L 3,11", "트레이로 숨기기", Hide));
        _header.Children.Add(actions);
        _layout.Children.Add(_header);
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        Grid.SetRow(_scroll, 1);
        _layout.Children.Add(_scroll);
        _footer = new TextBlock { FontSize = 9, Foreground = Theme.Muted, Margin = new Thickness(8, 4, 8, 7), Text = "최근 60초 · 1초 갱신" };
        Grid.SetRow(_footer, 2);
        _layout.Children.Add(_footer);
        _frame = new Border { Margin = new Thickness(4), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), BorderBrush = Theme.Border, Background = Theme.Background, Child = _layout };
        Content = _frame;
        _frame.MouseLeftButtonDown += Drag;
        _frame.ContextMenu = new ContextMenu();
        _frame.ContextMenuOpening += (_, _) => BuildMenu();
        _compact.SettingsRequested += (_, _) => ShowSettings();
        _expanded.SettingsRequested += (_, _) => ShowSettings();
        controller.SnapshotUpdated += Changed;
        controller.SettingsChanged += Changed;
        controller.PreferencesChanged += PreferencesChanged;
        SourceInitialized += (_, _) =>
        {
            WindowPlatform.Initialize(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
            ApplyAppearance();
        };
        Theme.Changed += ThemeChanged;
        Loaded += (_, _) =>
        {
            _loaded = true;
            Refresh();
            _controller.Settings.Placements.TryGetValue(_mode, out var position);
            Fit(position, true);
        };
        IsVisibleChanged += (_, _) => controller.SetVisible(IsVisible);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        DpiChanged += (_, _) => QueueFit(true);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            controller.SnapshotUpdated -= Changed;
            controller.SettingsChanged -= Changed;
            controller.PreferencesChanged -= PreferencesChanged;
            Theme.Changed -= ThemeChanged;
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            SystemEvents.PowerModeChanged -= PowerChanged;
            _settingsWindow?.Close();
        };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        Refresh();
    }

    private Button ActionButton(string geometry, string tooltip, Action action)
    {
        var icon = new Path { Data = Geometry.Parse(geometry), Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.25, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        icon.SetBinding(Shape.StrokeProperty, new Binding(nameof(Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var b = new Button { Content = icon, ToolTip = tooltip, Width = 28, Height = 28, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(b, tooltip);
        b.Click += (_, _) => action();
        return b;
    }

    private void Changed(object? sender, EventArgs e) => Refresh();
    // Saves follow this with SettingsChanged (one Refresh); previews only touch appearance.
    private void PreferencesChanged(object? sender, EventArgs e)
    {
        Theme.Apply(_controller.Settings.Theme);
        if (_mode != _controller.Settings.WidgetMode) ApplyMode(_controller.Settings.WidgetMode);
        ApplyAppearance();
    }

    private void Refresh()
    {
        var compact = _mode == WidgetMode.Compact;
        bool empty = !_controller.Settings.DisplayProfiles.For(_mode).Items.Values.Any(x => x.Visible);
        _header.Visibility = compact || empty ? Visibility.Collapsed : Visibility.Visible;
        _footer.Visibility = !empty && (!compact || _controller.IsDemo || _controller.SaveError is not null) ? Visibility.Visible : Visibility.Collapsed;
        _footer.Text = _controller.SaveError ?? (_controller.IsDemo ? "데모 · 실제 측정값이 아닙니다" : "최근 60초 · 1초 갱신");
        _footer.ToolTip = _controller.SaveError ?? _controller.CollectionError;
        _pin.Foreground = _controller.Settings.AlwaysOnTop ? Theme.Cpu : Theme.Muted;
        if (compact) { _compact.Refresh(_controller.Settings, _controller.Current, _controller.History.Samples); _scroll.Content = _compact; }
        else { _expanded.Refresh(_controller.Settings, _controller.Current, _controller.History.Samples); _scroll.Content = _expanded; }
        QueueFit();
    }

    public void SetMode(WidgetMode mode)
    {
        if (_mode == mode) return;
        SavePlacement();
        _controller.Settings.WidgetMode = mode;
        _controller.Save();
    }

    private void ApplyMode(WidgetMode mode)
    {
        var current = WindowPlatform.Capture(this);
        _mode = mode;
        Width = mode == WidgetMode.Compact ? 240 : 320;
        Refresh();
        _controller.Settings.Placements.TryGetValue(mode, out var restored);
        Fit(restored ?? current);
    }

    private void ApplyAppearance()
    {
        Topmost = _controller.Settings.AlwaysOnTop;
        WindowPlatform.ApplyTheme(this, Theme.IsDark);
        var background = Theme.Background.Color;
        var transparency = _controller.Settings.Transparency && !SystemParameters.HighContrast
            ? Math.Clamp(_controller.Settings.BackgroundTransparency, 0, 100) : 0;
        // A one-byte alpha floor keeps even an effectively clear background draggable.
        // Window.Opacity remains 1 so text, graphs and controls never fade with the background.
        byte alpha = (byte)Math.Max(1, Math.Round(255 * (1 - transparency / 100d), MidpointRounding.AwayFromZero));
        // Reuse one private (non-shared, non-frozen) brush; only its color changes.
        var color = Color.FromArgb(alpha, background.R, background.G, background.B);
        if (_frameBackground.Color != color) _frameBackground.Color = color;
        _frame.Background = _frameBackground;
        Background = Brushes.Transparent;
    }

    private void ThemeChanged(object? sender, EventArgs e) { ApplyAppearance(); Refresh(); }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case 0x0231: _sizeMove = true; break; // WM_ENTERSIZEMOVE: never fit/clamp while the user drags.
            case 0x0232: _sizeMove = false; QueueFit(true); break; // WM_EXITSIZEMOVE
            case 0x031A: // WM_THEMECHANGED
                Dispatcher.BeginInvoke(new Action(() => { Theme.RefreshSystemTheme(); ApplyAppearance(); }));
                break;
            case 0x001A: // WM_SETTINGCHANGE: only app color mode and high contrast affect appearance.
                if (wParam == 0x0043) Dispatcher.BeginInvoke(new Action(ApplyAppearance)); // SPI_SETHIGHCONTRAST
                else if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                    Dispatcher.BeginInvoke(new Action(() => { Theme.RefreshSystemTheme(); ApplyAppearance(); }));
                break;
        }
        return IntPtr.Zero;
    }

    private void QueueFit(bool force = false)
    {
        _forceFit |= force;
        if (!_loaded || _fitPending) return;
        _fitPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { _fitPending = false; Fit(); }));
    }

    private void Fit(WindowPlacement? position = null, bool initial = false)
    {
        if (!_loaded) return;
        if (_sizeMove) { _forceFit = true; return; } // WM_EXITSIZEMOVE queues the deferred fit.
        double height = MeasureDesiredHeight();
        // Steady-state ticks with unchanged content skip monitor lookup and SetWindowPos entirely.
        if (position is null && !initial && !_forceFit && height == _fittedHeight) return;
        _forceFit = false;
        _fittedHeight = height;
        WindowPlatform.FitAndPlace(this, height, position, initial);
    }

    internal double MeasureDesiredHeight()
    {
        // Measure the whole frame, including the actual rounded margins, border and scroll
        // presenter. Adding a fixed 10 DIP to separately measured children loses a physical
        // pixel at fractional DPI (for example 150%), creating a spurious vertical scrollbar.
        _frame.UpdateLayout();
        _frame.Measure(new Size(Width, double.PositiveInfinity));
        var scale = VisualTreeHelper.GetDpi(_frame).DpiScaleY;
        return Math.Ceiling(Math.Max(44, _frame.DesiredSize.Height) * scale - 1e-7) / scale;
    }

    public void ShowWidget()
    {
        Show();
        QueueFit(true);
    }

    public void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_controller);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void BuildMenu()
    {
        var menu = _frame.ContextMenu!;
        menu.Items.Clear();
        var size = new MenuItem { Header = "위젯 크기" };
        foreach (var mode in Enum.GetValues<WidgetMode>())
        {
            var item = new MenuItem { Header = mode == WidgetMode.Compact ? "작게" : "크게", IsCheckable = true, IsChecked = _mode == mode };
            item.Click += (_, _) => SetMode(mode);
            size.Items.Add(item);
        }
        menu.Items.Add(size);
        var top = new MenuItem { Header = "항상 위에 표시", IsCheckable = true, IsChecked = Topmost };
        top.Click += (_, _) => { _controller.Settings.AlwaysOnTop = top.IsChecked; _controller.Save(); };
        menu.Items.Add(top);
        Add("설정", ShowSettings);
        Add("업데이트 확인", _controller.RequestUpdate);
        menu.Items.Add(new Separator());
        Add("숨기기", Hide);
        Add("종료", () => ExitRequested?.Invoke(this, EventArgs.Empty));
        void Add(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != _frame; d = VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase || d is System.Windows.Controls.Primitives.ScrollBar) return;
        if (e.ClickCount == 2) { ShowSettings(); e.Handled = true; return; }
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); SavePlacement(); }
        catch (InvalidOperationException) { }
    }

    internal void SavePlacement()
    {
        if (!_loaded) return;
        var placement = WindowPlatform.Capture(this);
        // A click without movement (or an unchanged close) must not trigger a synchronous fsync save.
        if (_controller.Settings.Placements.TryGetValue(_mode, out var saved) && saved is not null
            && saved.Left == placement.Left && saved.Top == placement.Top) return;
        _controller.Settings.Placements[_mode] = placement;
        _controller.Save(false);
    }
    private void OnClosing(object? sender, CancelEventArgs e) { SavePlacement(); if (!AllowClose) { e.Cancel = true; Hide(); } }
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() => QueueFit(true)));
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) _controller.ResetSampling(); }
}
