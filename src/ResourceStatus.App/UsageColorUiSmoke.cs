using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ResourceStatus.App.Views;
using ResourceStatus.Core;
using AppThemeMode = ResourceStatus.Core.ThemeMode;

namespace ResourceStatus.App;

/// <summary>Checks color settings and actual widget text, gauges and recorded chart drawings.</summary>
internal static class UsageColorUiSmoke
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync(List<string> checks, string directory, bool renderingAvailable, AppThemeMode restoreTheme)
    {
        var host = new Window
        {
            Title = "Resource Status color validation", WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false, Width = 310, SizeToContent = SizeToContent.Height,
            Background = Theme.Background, UseLayoutRounding = true
        };
        try
        {
            await CheckSettingsAsync();
            host.Show();
            foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light, AppThemeMode.System })
            {
                Theme.Apply(theme);
                Assert(Contrast(Theme.UsageWarning.Color, Theme.Background.Color) >= 3
                    && Contrast(Theme.UsageCritical.Color, Theme.Background.Color) >= 3
                    && Theme.UsageWarning.Color != Theme.UsageCritical.Color,
                    $"{theme}: warning and critical colors remain distinct and readable against the widget background");
            }
            Theme.Apply(AppThemeMode.Dark);
            var crossingSettings = ForMetric(MetricKind.Cpu);
            var crossingHistory = new[] { Sample(Now.AddSeconds(-2), 50), Sample(Now, 100) };
            var crossingColors = new UsageColorTimeline();
            crossingColors.Update(crossingHistory, crossingSettings.UsageColors);
            var crossingView = new ExpandedWidgetView();
            await ShowView(crossingView, WidgetMode.Expanded, crossingSettings, Snapshot(Now, 100), crossingHistory, crossingColors);
            var crossingChart = Descendants<HistoryChart>(crossingView).Single();
            var retainedDrawingAvailable = VisualTreeHelper.GetDrawing(crossingChart) is not null;
            var crossingDrawings = ChartDrawings(crossingChart);
            var orangeEdges = crossingDrawings.Where(drawing => SameColor(drawing.Pen?.Brush, Theme.UsageWarning)).SelectMany(Edges).ToArray();
            var segmentStart = crossingChart.ActualWidth * (58d / 60);
            var segmentWidth = crossingChart.ActualWidth - segmentStart;
            var firstOrangeX = orangeEdges.Length == 0 ? double.NaN : orangeEdges.Min(edge => Math.Min(edge.From.X, edge.To.X));
            var lastOrangeX = orangeEdges.Length == 0 ? double.NaN : orangeEdges.Max(edge => Math.Max(edge.From.X, edge.To.X));
            File.WriteAllText(Path.Combine(directory, "usage-color-rendering.json"), JsonSerializer.Serialize(new
            {
                crossingChart.ActualWidth, crossingChart.ActualHeight, RetainedDrawingAvailable = retainedDrawingAvailable,
                CapturedOrangeEdges = orangeEdges.Length,
                OrangeStartX = double.IsFinite(firstOrangeX) ? (double?)firstOrangeX : null,
                OrangeEndX = double.IsFinite(lastOrangeX) ? (double?)lastOrangeX : null,
                ExpectedStartX = segmentStart + segmentWidth * .6, ExpectedEndX = segmentStart + segmentWidth * .9,
                ExpectedWarningBrush = Theme.UsageWarning.ToString(),
                Drawings = crossingDrawings.Select(item => new
                {
                    BrushType = item.Pen?.Brush?.GetType().FullName, Brush = item.Pen?.Brush?.ToString(),
                    GeometryType = item.Geometry.GetType().FullName, GeometryText = item.Geometry.ToString(),
                    Figures = PathGeometry.CreateFromGeometry(item.Geometry).Figures.Select(figure => new
                    {
                        Start = figure.StartPoint.ToString(), figure.IsClosed,
                        Segments = figure.Segments.Select(segment => new { Type = segment.GetType().FullName, Text = segment.ToString() })
                    })
                })
            }, new JsonSerializerOptions { WriteIndented = true }));
            Assert(orangeEdges.Length > 0
                && Math.Abs(firstOrangeX - (segmentStart + segmentWidth * .6)) < .02
                && Math.Abs(lastOrangeX - (segmentStart + segmentWidth * .9)) < .02,
                $"A two-sample 50% to 100% chart colors exactly the interpolated 80% through 95% portion orange "
                + $"(chart {crossingChart.ActualWidth:0.###}×{crossingChart.ActualHeight:0.###}; orange edges {orangeEdges.Length}, x {firstOrangeX:0.###}–{lastOrangeX:0.###})");
            foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light })
            {
                Theme.Apply(theme);
                foreach (var kind in Enum.GetValues<MetricKind>())
                {
                    var settings = ForMetric(kind);
                    var colors = new UsageColorTimeline();
                    var compact = new CompactWidgetView();
                    var expanded = new ExpandedWidgetView();
                    var history = new List<HistorySample>();
                    (double Value, UsageSeverity Level)[] states =
                    {
                        (50d, UsageSeverity.Normal), (80d, UsageSeverity.Warning),
                        (79d, UsageSeverity.Warning), (76d, UsageSeverity.Normal),
                        (95d, UsageSeverity.Critical), (94d, UsageSeverity.Critical),
                        (91d, UsageSeverity.Warning), (76d, UsageSeverity.Normal)
                    };
                    for (int i = 0; i < states.Length; i++)
                    {
                        var state = states[i];
                        var timestamp = Now.AddSeconds(i);
                        history.Add(Sample(timestamp, state.Value));
                        colors.Update(history, settings.UsageColors);
                        foreach (var mode in new[] { WidgetMode.Compact, WidgetMode.Expanded })
                        {
                            WidgetView view = mode == WidgetMode.Compact ? compact : expanded;
                            await ShowView(view, mode, settings, Snapshot(timestamp, state.Value), history, colors);
                            var number = Descendants<TextBlock>(view).Single(text => text.Text == $"{state.Value:0}%");
                            var gauge = Descendants<ThinGauge>(view).Single();
                            var expectedNumber = SeverityBrush(state.Level, Theme.Text);
                            var expectedGauge = SeverityBrush(state.Level, Accent(kind));
                            Assert(SameColor(number.Foreground, expectedNumber) && SameColor(gauge.FillBrush, expectedGauge)
                                && Descendants<TextBlock>(view).Any(text => text.Width == 12 && SameColor(text.Foreground, Accent(kind))),
                                $"{theme}/{mode}/{kind} at {state.Value}%: shared {state.Level} state colors the number and gauge while preserving the item identity");
                        }
                    }
                }

                var chartHistory = Enumerable.Range(0, 61).Select(index => Sample(Now.AddSeconds(index - 60),
                    index < 12 ? 50 : index < 24 ? 85 : index < 36 ? 98 : index < 48 ? 90 : 50)).ToArray();
                foreach (var mode in new[] { WidgetMode.Compact, WidgetMode.Expanded })
                {
                    var settings = ForMetric(MetricKind.Cpu);
                    var colors = new UsageColorTimeline();
                    colors.Update(chartHistory, settings.UsageColors);
                    WidgetView view = mode == WidgetMode.Compact ? new CompactWidgetView() : new ExpandedWidgetView();
                    await ShowView(view, mode, settings, Snapshot(Now, 50), chartHistory, colors);
                    var chart = Descendants<HistoryChart>(view).Single();
                    var drawings = ChartDrawings(chart);
                    Assert(HasColor(drawings, Theme.Cpu) && HasColor(drawings, Theme.UsageWarning) && HasColor(drawings, Theme.UsageCritical),
                        $"{theme}/{mode}: past graph segments retain normal, warning and critical colors after current usage becomes normal");
                    if (renderingAvailable)
                    {
                        var bitmap = RenderChart(chart);
                        Assert(HasPixel(bitmap, Theme.UsageWarning.Color) && HasPixel(bitmap, Theme.UsageCritical.Color),
                            $"{theme}/{mode}: warning and critical graph segments produce visible rendered pixels");
                        UiSmoke.SaveScreenshot(view, Path.Combine(directory, $"usage-colors-{theme.ToString().ToLowerInvariant()}-{mode.ToString().ToLowerInvariant()}.png"), 2);
                    }
                    var gapHistory = chartHistory.Select((sample, index) => index == 30 ? sample with { Cpu = null } : sample).ToArray();
                    colors.Update(gapHistory, settings.UsageColors);
                    await ShowView(view, mode, settings, Snapshot(Now, 50), gapHistory, colors);
                    var gapX = chart.ActualWidth / 2;
                    var colored = ChartDrawings(chart).Where(drawing => IsSeries(drawing, MetricKind.Cpu)).ToArray();
                    Assert(colored.Length > 0 && !colored.SelectMany(Edges).Any(edge => Math.Min(edge.From.X, edge.To.X) < gapX - .1
                        && Math.Max(edge.From.X, edge.To.X) > gapX + .1),
                        $"{theme}/{mode}: colored chart geometry leaves a missing sample as a gap");

                    settings.UsageColors[MetricKind.Cpu].Enabled = false;
                    colors.Update(chartHistory, settings.UsageColors);
                    await ShowView(view, mode, settings, Snapshot(Now, 50), chartHistory, colors);
                    Assert(!HasColor(ChartDrawings(chart), Theme.UsageWarning) && !HasColor(ChartDrawings(chart), Theme.UsageCritical),
                        $"{theme}/{mode}: disabling a metric's color alerts restores every historical segment to its usual color");
                }
                await CheckNetworkChartsAsync(theme, chartHistory);
            }
        }
        finally { host.Close(); Theme.Apply(restoreTheme); }

        async Task ShowView(WidgetView view, WidgetMode mode, AppSettings settings, SystemSnapshot snapshot,
            IReadOnlyList<HistorySample> history, UsageColorTimeline colors)
        {
            host.Width = mode == WidgetMode.Compact ? 230 : 310;
            host.Content = view;
            view.Refresh(settings, snapshot, history, colors);
            await Flush();
            view.Measure(new Size(host.Width, double.PositiveInfinity));
            view.Arrange(new Rect(0, 0, host.Width, view.DesiredSize.Height));
            view.UpdateLayout();
        }

        async Task CheckNetworkChartsAsync(AppThemeMode theme, HistorySample[] history)
        {
            foreach (var mode in new[] { WidgetMode.Compact, WidgetMode.Expanded })
            {
                var settings = ForMetric(MetricKind.Network);
                var colors = new UsageColorTimeline();
                colors.Update(history, settings.UsageColors);
                WidgetView view = mode == WidgetMode.Compact ? new CompactWidgetView() : new ExpandedWidgetView();
                await ShowView(view, mode, settings, Snapshot(Now, 50), history, colors);
                var chart = Descendants<HistoryChart>(view).Single();
                var alerts = ChartDrawings(chart).Where(drawing => SameColor(drawing.Pen?.Brush, Theme.UsageCritical)).ToArray();
                Assert(alerts.Any(drawing => drawing.Pen!.DashStyle.Dashes.Count == 0)
                    && alerts.Any(drawing => drawing.Pen!.DashStyle.Dashes.Count > 0)
                    && Descendants<TextBlock>(view).Any(text => text.Text.StartsWith("↓ ", StringComparison.Ordinal))
                    && Descendants<TextBlock>(view).Any(text => text.Text.StartsWith("↑ ", StringComparison.Ordinal)),
                    $"{theme}/{mode}: simultaneous network alerts preserve solid receive, dashed transmit and direction arrows");
                foreach (var direction in new[] { ElementIds.DownloadGraph, ElementIds.UploadGraph })
                {
                    settings.DisplayProfiles.For(mode).Items[MetricKind.Network].Elements = [direction];
                    await ShowView(view, mode, settings, Snapshot(Now, 50), history, colors);
                    chart = Descendants<HistoryChart>(view).Single();
                    alerts = ChartDrawings(chart).Where(drawing => SameColor(drawing.Pen?.Brush, Theme.UsageCritical)).ToArray();
                    Assert(alerts.Length > 0 && alerts.All(drawing => (drawing.Pen!.DashStyle.Dashes.Count > 0) == (direction == ElementIds.UploadGraph)),
                        $"{theme}/{mode}/{direction}: hiding the other network graph preserves the remaining direction's stroke style");
                }

                settings = ForMetric(MetricKind.Network);
                foreach (double? usage in new double?[] { 20, null })
                {
                    var lowUsage = history.Select(sample => sample with { LinkUsage = usage, Download = 100_000_000_000, Upload = 50_000_000_000 }).ToArray();
                    colors.Update(lowUsage, settings.UsageColors);
                    await ShowView(view, mode, settings, Snapshot(Now, usage, 100_000_000_000), lowUsage, colors);
                    chart = Descendants<HistoryChart>(view).Single();
                    Assert(!HasColor(ChartDrawings(chart), Theme.UsageWarning) && !HasColor(ChartDrawings(chart), Theme.UsageCritical)
                        && Descendants<TextBlock>(view).Any(text => text.Text.StartsWith("↓ ", StringComparison.Ordinal) && SameColor(text.Foreground, Theme.Download)),
                        $"{theme}/{mode}: network alert colors use {(usage is null ? "known link utilization only" : "link utilization rather than byte rate")}");
                }
            }
        }

        async Task CheckSettingsAsync()
        {
            string path = Path.Combine(directory, "usage-color-settings.json");
            File.WriteAllText(path, "{\"Theme\":\"Dark\"}"); // Existing settings without the newly added fields.
            using var controller = new AppController(path, demo: true);
            var settingsWindow = new SettingsWindow(controller);
            try
            {
                settingsWindow.Show();
                settingsWindow.ProfileTabs.SelectedIndex = 3;
                await Flush();
                var tab = (TabItem)settingsWindow.ProfileTabs.Items[3];
                var content = (DependencyObject)tab.Content;
                Assert(tab.Name == "UsageColorsTab" && Enum.GetValues<MetricKind>().All(kind =>
                    Control<CheckBox>("UsageColorEnabled" + kind).IsChecked == true
                    && Control<TextBox>("UsageColorWarning" + kind).Text == "80"
                    && Control<TextBox>("UsageColorCritical" + kind).Text == "95"),
                    "Older settings open with enabled 80% warning and 95% critical controls for all four metrics");
                var profiles = JsonSerializer.Serialize(controller.Settings.DisplayProfiles);
                var snapshot = Snapshot(Now, 75);
                controller.History.Add(snapshot with { Timestamp = Now.AddSeconds(-2) });
                controller.History.Add(snapshot with { Timestamp = Now.AddSeconds(-1) });
                controller.History.Add(snapshot);
                var history = controller.History.Samples.ToArray();
                var warning = Control<TextBox>("UsageColorWarningCpu");
                var critical = Control<TextBox>("UsageColorCriticalCpu");
                SetText(warning, "70"); SetText(critical, "90"); Commit(critical);
                await Flush();
                var saved = new SettingsStore(path).Load();
                Assert(saved.UsageColors[MetricKind.Cpu].WarningThreshold == 70 && saved.UsageColors[MetricKind.Cpu].CriticalThreshold == 90
                    && controller.Settings.UsageColors[MetricKind.Ram].WarningThreshold == 80,
                    "Accessible threshold editing commits and persists a valid metric-specific pair");
                var validation = Control<TextBlock>("UsageColorValidationCpu");
                string previousMessage = validation.Text;
                SetText(warning, "95"); Commit(warning);
                saved = new SettingsStore(path).Load();
                Assert(saved.UsageColors[MetricKind.Cpu].WarningThreshold == 70 && saved.UsageColors[MetricKind.Cpu].CriticalThreshold == 90
                    && controller.Settings.UsageColors[MetricKind.Cpu].WarningThreshold == 70 && validation.Text != previousMessage,
                    "A warning threshold above the critical threshold shows feedback without changing saved or active values");
                SetText(warning, "70"); Commit(warning);
                var enabled = Control<CheckBox>("UsageColorEnabledCpu");
                Toggle(enabled);
                Assert(!warning.IsEnabled && !critical.IsEnabled && !new SettingsStore(path).Load().UsageColors[MetricKind.Cpu].Enabled
                    && controller.Settings.UsageColors[MetricKind.Cpu].WarningThreshold == 70
                    && controller.Settings.UsageColors[MetricKind.Cpu].CriticalThreshold == 90,
                    "Disabling color alerts preserves thresholds, disables their editors and persists the choice");
                Toggle(enabled);
                Assert(warning.IsEnabled && critical.IsEnabled && new SettingsStore(path).Load().UsageColors[MetricKind.Cpu].Enabled,
                    "Re-enabling color alerts restores editable thresholds and persists the choice");
                Assert(JsonSerializer.Serialize(controller.Settings.DisplayProfiles) == profiles && controller.History.Samples.SequenceEqual(history),
                    "Shared color settings leave both display profiles and existing measurement history intact");
                foreach (var mode in new[] { WidgetMode.Compact, WidgetMode.Expanded })
                {
                    var display = ForMetric(MetricKind.Cpu);
                    display.UsageColors = controller.Settings.UsageColors;
                    WidgetView view = mode == WidgetMode.Compact ? new CompactWidgetView() : new ExpandedWidgetView();
                    view.Refresh(display, snapshot, controller.History.Samples, controller.UsageColors);
                    double width = mode == WidgetMode.Compact ? 230 : 310;
                    view.Measure(new Size(width, double.PositiveInfinity));
                    view.Arrange(new Rect(0, 0, width, view.DesiredSize.Height));
                    view.UpdateLayout();
                    var number = Descendants<TextBlock>(view).Single(text => text.Text == "75%");
                    Assert(SameColor(number.Foreground, Theme.UsageWarning) && SameColor(Descendants<ThinGauge>(view).Single().FillBrush, Theme.UsageWarning),
                        $"The saved shared CPU threshold immediately gives {mode} the same warning color");
                }
                T Control<T>(string name) where T : FrameworkElement => Descendants<T>(content).Single(element => element.Name == name);
            }
            finally { settingsWindow.Close(); }
        }

        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Usage color UI assertion failed: " + message);
            checks.Add(message);
        }
    }

    private static AppSettings ForMetric(MetricKind kind)
    {
        var settings = new AppSettings();
        foreach (var profile in new[] { settings.DisplayProfiles.Compact, settings.DisplayProfiles.Expanded })
        {
            foreach (var pair in profile.Items) pair.Value.Visible = pair.Key == kind;
            profile.Items[kind].Elements = kind == MetricKind.Network
                ? [ElementIds.Usage, ElementIds.Gauge, ElementIds.Download, ElementIds.Upload, ElementIds.DownloadGraph, ElementIds.UploadGraph]
                : [ElementIds.Usage, ElementIds.Gauge, ElementIds.Graph];
        }
        return settings;
    }

    private static HistorySample Sample(DateTimeOffset timestamp, double? value) =>
        new(timestamp, value, value, value, 20_000, 10_000, "color-gpu", "color-network", value);
    private static SystemSnapshot Snapshot(DateTimeOffset timestamp, double? value, double rate = 20_000)
    {
        var usage = value is double ready ? Reading<double>.Ready(ready) : Reading<double>.Missing(ReadingState.Unsupported);
        var used = Reading<ulong>.Ready(8ul << 30);
        var total = Reading<ulong>.Ready(16ul << 30);
        return new(timestamp, new CpuSnapshot("Color test CPU", usage), new RamSnapshot(usage, used, total),
            new GpuSnapshot("color-gpu", "Color test GPU", usage, used, total, Reading<ulong>.Ready(0)),
            new NetworkSnapshot("color-network", "Color test adapter", Reading<double>.Ready(rate), Reading<double>.Ready(rate / 2),
                usage, Reading<ulong>.Ready(1_000_000_000), Reading<ulong>.Ready(1_000_000_000), "인터넷 연결"));
    }
    private static Brush SeverityBrush(UsageSeverity severity, Brush normal) => severity switch
    { UsageSeverity.Warning => Theme.UsageWarning, UsageSeverity.Critical => Theme.UsageCritical, _ => normal };
    private static Brush Accent(MetricKind kind) => kind switch
    { MetricKind.Cpu => Theme.Cpu, MetricKind.Ram => Theme.Ram, MetricKind.Gpu => Theme.Gpu, _ => Theme.Download };
    private static bool SameColor(Brush? left, Brush right) => left is SolidColorBrush a && right is SolidColorBrush b && a.Color == b.Color;
    private static bool HasColor(IEnumerable<GeometryDrawing> drawings, Brush brush) => drawings.Any(drawing => SameColor(drawing.Pen?.Brush, brush));
    private static bool IsSeries(GeometryDrawing drawing, MetricKind kind) => SameColor(drawing.Pen?.Brush, Accent(kind))
        || SameColor(drawing.Pen?.Brush, Theme.UsageWarning) || SameColor(drawing.Pen?.Brush, Theme.UsageCritical);
    private static GeometryDrawing[] ChartDrawings(HistoryChart chart)
    {
        chart.UpdateLayout();
        // Disconnected desktop sessions can skip WPF's compositor and leave the retained drawing
        // empty. Exercise the actual production renderer with a recording context instead, so the
        // geometry assertions also run without a functioning desktop or bitmap renderer.
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
            typeof(HistoryChart).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(chart, [context]);
        return Drawings(drawing).OfType<GeometryDrawing>().Where(item => item.Pen is not null).ToArray();
    }
    private static IEnumerable<Drawing> Drawings(Drawing? drawing)
    {
        if (drawing is null) yield break;
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var descendant in Drawings(child)) yield return descendant;
    }
    private static IEnumerable<(Point From, Point To)> Edges(GeometryDrawing drawing)
    {
        // All production chart paths are straight line segments. Flattening can return no figures
        // for these unfilled paths in a disconnected session; preserve their original strokes.
        foreach (var figure in PathGeometry.CreateFromGeometry(drawing.Geometry).Figures)
        {
            var previous = figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                if (segment is LineSegment line) { yield return (previous, line.Point); previous = line.Point; }
                else if (segment is PolyLineSegment lines)
                    foreach (var point in lines.Points) { yield return (previous, point); previous = point; }
            }
        }
    }
    private static RenderTargetBitmap RenderChart(HistoryChart chart)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Theme.Background, null, new Rect(chart.RenderSize));
            context.DrawRectangle(new VisualBrush(chart), null, new Rect(chart.RenderSize));
        }
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(chart.ActualWidth * 2)),
            Math.Max(1, (int)Math.Ceiling(chart.ActualHeight * 2)), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }
    private static bool HasPixel(BitmapSource bitmap, Color color)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] >= 250 && Math.Abs(pixels[i] - color.B) < 12
                && Math.Abs(pixels[i + 1] - color.G) < 12 && Math.Abs(pixels[i + 2] - color.R) < 12) return true;
        return false;
    }
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value) { double s = value / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        static double Luminance(Color color) => .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        return (Math.Max(Luminance(a), Luminance(b)) + .05) / (Math.Min(Luminance(a), Luminance(b)) + .05);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void SetText(TextBox input, string value)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(input) ?? new TextBoxAutomationPeer(input);
        if (peer.GetPattern(PatternInterface.Value) is not IValueProvider provider)
            throw new InvalidOperationException("The threshold editor must expose UI Automation ValuePattern.");
        provider.SetValue(value);
    }
    private static void Commit(TextBox input) => input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(input)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
    private static void Toggle(CheckBox input)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(input) ?? new CheckBoxAutomationPeer(input);
        if (peer.GetPattern(PatternInterface.Toggle) is not IToggleProvider provider)
            throw new InvalidOperationException("The color alert switch must expose UI Automation TogglePattern.");
        provider.Toggle();
    }
    private static async Task Flush() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
}
