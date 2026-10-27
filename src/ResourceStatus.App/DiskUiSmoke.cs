using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ResourceStatus.App.Views;
using ResourceStatus.Core;
using AppThemeMode = ResourceStatus.Core.ThemeMode;

namespace ResourceStatus.App;

/// <summary>Disk selection, direction-specific drawing and sizing checks using actual WPF controls.</summary>
internal static class DiskUiSmoke
{
    private static readonly DiskDeviceOption First = new("disk:test-first", "디스크 0 (C:)", "NVMe test device with a deliberately very long model name", 0);
    private static readonly DiskDeviceOption Second = new("disk:test-second", "디스크 1 (D:, E:)", "Second physical disk", 1);
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync(List<string> checks, string directory, bool renderingAvailable, AppThemeMode restoreTheme)
    {
        await CheckSettings();
        var host = new Window
        {
            Title = "Resource Status disk validation", WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false, Width = 310, SizeToContent = SizeToContent.Height,
            Background = Theme.Background, UseLayoutRounding = true
        };
        try
        {
            host.Show();
            var history = new HistoryBuffer();
            for (int index = 0; index <= 60; index++) history.Add(Snapshot(Now.AddSeconds(index - 60)));
            foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light, AppThemeMode.System })
            {
                Theme.Apply(theme);
                foreach (var mode in new[] { WidgetMode.Compact, WidgetMode.Expanded })
                {
                    var settings = DiskSettings();
                    WidgetView view = mode == WidgetMode.Compact ? new CompactWidgetView() : new ExpandedWidgetView();
                    await ShowView(view, mode, settings, Snapshot(Now), history.Samples);
                    var rows = Descendants<DiskMetricRow>(view).ToArray();
                    Assert(rows.Select(row => row.DeviceId).SequenceEqual(new[] { DiskCatalog.TotalId, First.Id, Second.Id }),
                        $"{theme}/{mode}: total and selected physical disks have separate rows ordered by disk number");
                    Assert(Texts(rows[0]).Contains("읽기 3 GB/s") && Texts(rows[1]).Contains("읽기 1 GB/s")
                        && Texts(rows[2]).Contains("읽기 2 GB/s") && Descendants<HistoryChart>(view).Count() == 3,
                        $"{theme}/{mode}: total is not added again and byte rates above 100 retain GB/s units");
                    Assert(Descendants<TextBlock>(rows[1]).Any(text => text.Text == First.Model
                        && Equals(text.ToolTip, First.Model) && text.TextTrimming == TextTrimming.CharacterEllipsis),
                        $"{theme}/{mode}: long disk models retain a full tooltip with trimmed text");
                    foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
                    {
                        view.LayoutTransform = new ScaleTransform(scale, scale);
                        Layout(view, (mode == WidgetMode.Compact ? 230 : 310) * scale);
                        Assert(Descendants<DiskMetricRow>(view).All(row => double.IsFinite(row.ActualHeight) && row.ActualHeight > 0)
                            && Descendants<HistoryChart>(view).All(chart => chart.ActualWidth > 24 && chart.ActualHeight == (mode == WidgetMode.Compact ? 28 : 56)),
                            $"{theme}/{mode}/{scale:P0}: disk rows and graphs keep valid bounds at scaled layout");
                    }
                    view.LayoutTransform = Transform.Identity;
                    await ShowView(view, mode, settings, Snapshot(Now), history.Samples);
                    if (renderingAvailable)
                        UiSmoke.SaveScreenshot(view, Path.Combine(directory, $"disks-{theme.ToString().ToLowerInvariant()}-{mode.ToString().ToLowerInvariant()}.png"), 2);

                    foreach (var direction in new[] { ElementIds.ReadGraph, ElementIds.WriteGraph })
                    {
                        settings.DisplayProfiles.For(mode).Items[MetricKind.Disk].Elements = [direction];
                        await ShowView(view, mode, settings, Snapshot(Now), history.Samples);
                        foreach (var row in Descendants<DiskMetricRow>(view))
                        {
                            var chart = Descendants<HistoryChart>(row).Single();
                            var drawing = RecordChart(chart);
                            var strokes = Drawings(drawing).OfType<GeometryDrawing>()
                                .Where(item => SameColor(item.Pen?.Brush, Theme.Download) || SameColor(item.Pen?.Brush, Theme.Upload)).ToArray();
                            bool writing = direction == ElementIds.WriteGraph;
                            Assert(strokes.Length > 0 && strokes.All(item => (item.Pen!.DashStyle.Dashes.Count > 0) == writing)
                                && !Texts(row).Any(text => text.StartsWith("읽기 ", StringComparison.Ordinal) || text.StartsWith("쓰기 ", StringComparison.Ordinal)),
                                $"{theme}/{mode}/{row.DeviceId}/{direction}: graph-only layout keeps only the requested direction and stroke style");
                            var axis = (FormattedText?)typeof(HistoryChart).GetField("_axis", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chart);
                            Assert(axis?.Text.EndsWith("B/s", StringComparison.Ordinal) == true,
                                $"{theme}/{mode}/{row.DeviceId}/{direction}: graph-only layout retains a byte-rate axis");
                        }
                    }
                    settings.DisplayProfiles.For(mode).Items[MetricKind.Disk].Elements = [ElementIds.Read, ElementIds.Write, ElementIds.ReadGraph, ElementIds.WriteGraph];
                    var errorSnapshot = Snapshot(Now);
                    var mixed = errorSnapshot.Disks.ToDictionary(pair => pair.Key, pair => pair.Value);
                    mixed[First.Id] = mixed[First.Id] with { ReadBytesPerSecond = Reading<double>.Missing(ReadingState.AccessDenied) };
                    await ShowView(view, mode, settings, errorSnapshot with { Disks = mixed }, history.Samples);
                    var failed = Descendants<DiskMetricRow>(view).Single(row => row.DeviceId == First.Id);
                    Assert(Texts(failed).Contains("읽기 —") && Texts(failed).Contains("쓰기 500 MB/s") && Texts(failed).Contains("접근 거부"),
                        $"{theme}/{mode}: one direction failing leaves the other rate intact and shows its error");
                    mixed.Remove(Second.Id);
                    await ShowView(view, mode, settings, errorSnapshot with { Disks = mixed }, history.Samples);
                    var absent = Descendants<DiskMetricRow>(view).Single(row => row.DeviceId == Second.Id);
                    Assert(Texts(absent).Contains(Second.Name) && Texts(absent).Contains("연결 안 됨") && Texts(absent).Contains("읽기 —"),
                        $"{theme}/{mode}: a disconnected selected disk retains its identity and never displays zero as a measurement");
                    var gap = history.Samples.Select((sample, index) => index == 30 ? sample with
                    {
                        Disks = new Dictionary<string, DiskHistorySample> { [First.Id] = new(null, 500_000_000) }
                    } : sample).ToArray();
                    await ShowView(view, mode, settings, Snapshot(Now), gap);
                    var gapChart = Descendants<HistoryChart>(Descendants<DiskMetricRow>(view).Single(row => row.DeviceId == First.Id)).Single();
                    var readEdges = Drawings(RecordChart(gapChart)).OfType<GeometryDrawing>()
                        .Where(item => SameColor(item.Pen?.Brush, Theme.Download)).SelectMany(Edges).ToArray();
                    double middle = gapChart.ActualWidth / 2;
                    Assert(readEdges.Length > 0 && !readEdges.Any(edge => Math.Min(edge.From.X, edge.To.X) < middle - .1 && Math.Max(edge.From.X, edge.To.X) > middle + .1),
                        $"{theme}/{mode}: disk graph geometry leaves missing direction samples as gaps");
                }
            }
        }
        finally { host.Close(); Theme.Apply(restoreTheme); }
        await CheckWindowSizing();

        async Task ShowView(WidgetView view, WidgetMode mode, AppSettings settings, SystemSnapshot snapshot, IReadOnlyList<HistorySample> samples)
        {
            host.Width = mode == WidgetMode.Compact ? 230 : 310;
            host.Content = view;
            view.Refresh(settings, snapshot, samples);
            await Flush();
            Layout(view, host.Width);
        }

        async Task CheckSettings()
        {
            string path = Path.Combine(directory, "disk-ui-settings.json");
            new SettingsStore(path).Save(new AppSettings());
            using var controller = new AppController(path, demo: true);
            SetDevices(controller, [DiskCatalog.TotalDevice, Second, First]);
            var settings = new SettingsWindow(controller);
            try
            {
                settings.Show();
                settings.ProfileTabs.SelectedIndex = 0;
                await Flush();
                var content = (DependencyObject)((TabItem)settings.ProfileTabs.Items[0]).Content;
                CheckBox Source(string id) => Descendants<CheckBox>(content).Single(check => Equals(check.Tag, id));
                var total = Source(DiskCatalog.TotalId);
                var first = Source(First.Id);
                var second = Source(Second.Id);
                var visible = Descendants<CheckBox>(content).Single(check => check.Name == "VisibleDisk");
                var expanded = JsonSerializer.Serialize(controller.Settings.DisplayProfiles.Expanded);
                controller.History.Add(Snapshot(Now));
                var history = controller.History.Samples.ToArray();
                Toggle(first); Toggle(second);
                Assert(controller.Settings.DisplayProfiles.Compact.DiskSources.Count == 3 && total.IsChecked == true
                    && new SettingsStore(path).Load().DisplayProfiles.Compact.DiskSources.Count == 3
                    && JsonSerializer.Serialize(controller.Settings.DisplayProfiles.Expanded) == expanded,
                    "Disk settings immediately persist simultaneous total and individual selections independently for each mode");
                controller.Save();
                Assert(ReferenceEquals(Source(First.Id), first) && ReferenceEquals(Source(Second.Id), second),
                    "Unchanged device updates preserve the existing disk checkbox instances");
                Toggle(total); Toggle(first); Toggle(second);
                Assert(second.IsChecked == true && controller.Settings.DisplayProfiles.Compact.DiskSources.Single().Id == Second.Id,
                    "The last selected disk cannot be unchecked while disk display is enabled");
                Toggle(visible);
                Assert(!second.IsEnabled && second.IsChecked == true && !controller.Settings.DisplayProfiles.Compact.Items[MetricKind.Disk].Visible,
                    "Hiding disks disables source choices while preserving the selected ID");
                Toggle(visible);
                Assert(second.IsEnabled && second.IsChecked == true, "Showing disks restores source controls and selected IDs");
                var diskElements = Descendants<CheckBox>(content).Where(check => AutomationProperties.GetName(check).StartsWith("디스크 ", StringComparison.Ordinal)
                    && check.Tag is string id && DisplayCatalog.Elements(MetricKind.Disk).Any(element => element.Id == id)).ToArray();
                var readGraph = diskElements.Single(check => Equals(check.Tag, ElementIds.ReadGraph));
                Toggle(readGraph);
                foreach (var element in diskElements.Where(check => !ReferenceEquals(check, readGraph) && check.IsChecked == true)) Toggle(element);
                Toggle(readGraph);
                Assert(readGraph.IsChecked == true && controller.Settings.DisplayProfiles.Compact.Items[MetricKind.Disk].Elements.SetEquals([ElementIds.ReadGraph]),
                    "Disk settings support a read graph alone and protect its final display element");
                SetDevices(controller, [DiskCatalog.TotalDevice, First]);
                controller.Save();
                Assert(ReferenceEquals(Source(Second.Id), second) && ((TextBlock)second.Content).Text.Contains("연결 안 됨", StringComparison.Ordinal)
                    && second.IsChecked == true && new SettingsStore(path).Load().DisplayProfiles.Compact.DiskSources.Single().Name == Second.Name,
                    "Disconnecting a selected disk preserves its checkbox, selection and saved display name");
                SetDevices(controller, [DiskCatalog.TotalDevice, First, Second with { Number = 4, Name = "디스크 4 (F:)" }]);
                controller.Save();
                Assert(ReferenceEquals(Source(Second.Id), second) && ((TextBlock)second.Content).Text == "디스크 4 (F:)" && second.IsChecked == true,
                    "The same disk ID stays selected after a disk number and drive-letter change");
                var restore = Descendants<Button>(content).Single(button => Equals(button.Content, "이 모드 기본값 복원"));
                restore.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert(controller.Settings.DisplayProfiles.Compact.DiskSources.Single().Id == DiskCatalog.TotalId
                    && controller.Settings.DisplayProfiles.Compact.Items[MetricKind.Disk].Elements.SetEquals([ElementIds.Read, ElementIds.Write])
                    && JsonSerializer.Serialize(controller.Settings.DisplayProfiles.Expanded) == expanded
                    && controller.History.Samples.SequenceEqual(history),
                    "Restoring compact defaults resets its disk selection and elements without changing the other mode or measurement history");
                settings.ProfileTabs.SelectedIndex = 3;
                await Flush();
                Assert(!Descendants<CheckBox>((DependencyObject)((TabItem)settings.ProfileTabs.Items[3]).Content).Any(check => check.Name == "UsageColorEnabledDisk"),
                    "Disk byte rates are excluded from percentage-based color alert controls");
            }
            finally { settings.Close(); }

            string legacyPath = Path.Combine(directory, "disk-ui-legacy-settings.json");
            File.WriteAllText(legacyPath, "{\"DisplayProfiles\":{\"Compact\":{\"Items\":{\"Cpu\":{\"Visible\":false},\"Ram\":{\"Visible\":false},\"Gpu\":{\"Visible\":false},\"Network\":{\"Visible\":false}}}}}");
            using var legacy = new AppController(legacyPath, demo: true);
            var legacyWindow = new SettingsWindow(legacy);
            try
            {
                legacyWindow.Show();
                legacyWindow.ProfileTabs.SelectedIndex = 0;
                await Flush();
                var content = (DependencyObject)((TabItem)legacyWindow.ProfileTabs.Items[0]).Content;
                Assert(!legacy.Settings.DisplayProfiles.Compact.Items.Values.Any(item => item.Visible)
                    && Descendants<CheckBox>(content).Single(check => check.Name == "VisibleDisk").IsChecked == false
                    && !Descendants<CheckBox>(content).Single(check => Equals(check.Tag, DiskCatalog.TotalId)).IsEnabled,
                    "Existing all-hidden settings remain all-hidden when the new disk controls open");
            }
            finally { legacyWindow.Close(); }
        }

        async Task CheckWindowSizing()
        {
            string path = Path.Combine(directory, "disk-window-settings.json");
            var initial = DiskSettings();
            foreach (var profile in new[] { initial.DisplayProfiles.Compact, initial.DisplayProfiles.Expanded })
            {
                profile.DiskSources = [new() { Id = DiskCatalog.TotalId, Name = DiskCatalog.TotalDevice.Name }];
                profile.Items[MetricKind.Disk].Elements = [ElementIds.Read, ElementIds.Write];
            }
            new SettingsStore(path).Save(initial);
            using var controller = new AppController(path, demo: true);
            var window = new MainWindow(controller) { AllowClose = true };
            try
            {
                window.Show();
                foreach (var mode in new[] { WidgetMode.Compact, WidgetMode.Expanded })
                {
                    window.SetMode(mode);
                    await Flush();
                    Assert(window.WidgetScroll.ComputedVerticalScrollBarVisibility != Visibility.Visible && window.WidgetScroll.ScrollableHeight < .1,
                        $"{mode}: one disk with numeric rates does not create an unnecessary scrollbar");
                    double natural = window.ActualHeight;
                    controller.Settings.LockWidgetSize = true;
                    controller.Save();
                    var profile = controller.Settings.DisplayProfiles.For(mode);
                    profile.DiskSources.AddRange(Enumerable.Range(0, 8).Select(index => new DiskSourceSelection { Id = "disk:sizing-" + index, Name = "디스크 " + index }));
                    profile.Items[MetricKind.Disk].Elements = [ElementIds.Read, ElementIds.Write, ElementIds.ReadGraph, ElementIds.WriteGraph, ElementIds.Name];
                    controller.Save();
                    await Flush();
                    Assert(Math.Abs(window.ActualHeight - natural) < 1 && window.WidgetScroll.ScrollableHeight > 0,
                        $"{mode}: multiple selected disks honor fixed height and enable scrolling only for overflow");
                    controller.Settings.LockWidgetSize = false;
                    profile.DiskSources = [new() { Id = DiskCatalog.TotalId, Name = DiskCatalog.TotalDevice.Name }];
                    profile.Items[MetricKind.Disk].Elements = [ElementIds.Read];
                    controller.Save();
                    await Flush();
                    Assert(window.WidgetScroll.ScrollableHeight < .1 && window.ActualHeight <= natural + 1,
                        $"{mode}: removing disk elements and rows removes blank space and unnecessary scrolling");
                }
            }
            finally { window.Close(); }
        }

        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Disk UI assertion failed: " + message);
            checks.Add(message);
        }
    }

    private static AppSettings DiskSettings()
    {
        var settings = new AppSettings();
        foreach (var profile in new[] { settings.DisplayProfiles.Compact, settings.DisplayProfiles.Expanded })
        {
            foreach (var pair in profile.Items) pair.Value.Visible = pair.Key == MetricKind.Disk;
            profile.Items[MetricKind.Disk].Elements = DisplayCatalog.Elements(MetricKind.Disk).Select(element => element.Id).ToHashSet();
            profile.DiskSources = new[] { Second, DiskCatalog.TotalDevice, First }.Select(device => new DiskSourceSelection { Id = device.Id, Name = device.Name }).ToList();
        }
        return settings;
    }

    private static SystemSnapshot Snapshot(DateTimeOffset time) => AppController.EmptySnapshot() with
    {
        Timestamp = time,
        Disks = new Dictionary<string, DiskSnapshot>
        {
            [DiskCatalog.TotalId] = new(DiskCatalog.TotalDevice, Reading<double>.Ready(3_000_000_000), Reading<double>.Ready(750_000_000)),
            [First.Id] = new(First, Reading<double>.Ready(1_000_000_000), Reading<double>.Ready(500_000_000)),
            [Second.Id] = new(Second, Reading<double>.Ready(2_000_000_000), Reading<double>.Ready(250_000_000))
        }
    };
    private static void SetDevices(AppController controller, IReadOnlyList<DiskDeviceOption> devices) =>
        typeof(AppController).GetProperty(nameof(AppController.Disks))!.SetValue(controller, devices);
    private static IEnumerable<string> Texts(DependencyObject root) => Descendants<TextBlock>(root).Where(text => text.Visibility == Visibility.Visible).Select(text => text.Text);
    private static bool SameColor(Brush? first, Brush second) => first is SolidColorBrush a && second is SolidColorBrush b && a.Color == b.Color;
    private static DrawingGroup RecordChart(HistoryChart chart)
    {
        var group = new DrawingGroup();
        using (var context = group.Open()) typeof(HistoryChart).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(chart, [context]);
        return group;
    }
    private static IEnumerable<Drawing> Drawings(Drawing drawing)
    {
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var descendant in Drawings(child)) yield return descendant;
    }
    private static IEnumerable<(Point From, Point To)> Edges(GeometryDrawing drawing)
    {
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
    private static void Layout(FrameworkElement view, double width)
    {
        view.Measure(new Size(width, double.PositiveInfinity));
        view.Arrange(new Rect(0, 0, width, view.DesiredSize.Height));
        view.UpdateLayout();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Toggle(CheckBox input)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(input) ?? new CheckBoxAutomationPeer(input);
        if (peer.GetPattern(PatternInterface.Toggle) is not IToggleProvider provider) throw new InvalidOperationException("Disk choices must support UIA TogglePattern.");
        provider.Toggle();
    }
    private static async Task Flush() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
}
