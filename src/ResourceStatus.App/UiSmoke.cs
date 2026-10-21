using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ResourceStatus.App.Views;
using ResourceStatus.Core;
using AppThemeMode = ResourceStatus.Core.ThemeMode;

namespace ResourceStatus.App;

/// <summary>Opt-in integration checks against actual WPF controls, with isolated demo settings.</summary>
internal static class UiSmoke
{
    public static async Task RunAsync(AppController controller, MainWindow window, string output)
    {
        if (!controller.IsDemo) throw new InvalidOperationException("UI smoke checks require demo mode.");
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // Shell-launched hidden STARTUPINFO affects the first Show; a subsequent WPF show
        // activates the render channel so the opt-in UI test can actually exercise pixels.
        window.Hide();
        window.Show();
        var checks = new List<string>();
        var skipped = new List<string>();
        bool renderingAvailable = false;
        var directory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(directory);
        SettingsWindow? settingsWindow = null;
        try
        {
            await Task.Delay(2300);
            controller.SetVisible(false);
            await Task.Delay(200);
            await FlushLayout();
            renderingAvailable = CanRenderPixels();
            if (!renderingAvailable)
                skipped.Add("The Windows session returned a fully transparent bitmap even for an independent solid-red DrawingVisual. Screenshot and bitmap-scale pixel checks were skipped; control interaction and layout assertions still ran.");
            Assert(controller.Current.Cpu.Usage.IsReady && controller.History.Samples.Count >= 2,
                "The demo collector publishes readings and seeded history", checks);
            var snapshot = controller.Current;
            var history = controller.History.Samples.ToArray();

            foreach (var mode in new[] { WidgetMode.Compact, WidgetMode.Expanded })
            {
                window.SetMode(mode);
                await FlushLayout();
                var naturalHeight = window.MeasureDesiredHeight();
                window.UpdateLayout();
                var scroll = window.WidgetScroll;
                var constrained = window.ActualHeight + .1 < naturalHeight;
                Assert(constrained
                        ? scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible && scroll.ScrollableHeight > 0
                        : scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible && scroll.ScrollableHeight < .1,
                    $"{mode}: default content only scrolls when constrained by the actual work area (height {window.ActualHeight:0.###}, natural height {naturalHeight:0.###})", checks);
                AssertWidgetSizing(window, mode, snapshot, history, checks);
            }

            // Every selectable element must render independently, with no dependency on a number or chart.
            foreach (var mode in Enum.GetValues<WidgetMode>())
            {
                foreach (var kind in Enum.GetValues<MetricKind>())
                {
                    foreach (var element in DisplayCatalog.Elements(kind))
                    {
                        var settings = new AppSettings();
                        var profile = settings.DisplayProfiles.For(mode);
                        foreach (var pair in profile.Items) pair.Value.Visible = pair.Key == kind;
                        profile.Items[kind].Elements = [element.Id];
                        WidgetView view = mode == WidgetMode.Compact ? new CompactWidgetView() : new ExpandedWidgetView();
                        view.Refresh(settings, snapshot, history);
                        Layout(view, mode == WidgetMode.Compact ? 230 : 310);
                        var hasChart = element.Id is ElementIds.Graph or ElementIds.DownloadGraph or ElementIds.UploadGraph;
                        var hasGauge = element.Id == ElementIds.Gauge;
                        Assert(Descendants<HistoryChart>(view).Count() == (hasChart ? 1 : 0)
                            && Descendants<ThinGauge>(view).Count() == (hasGauge ? 1 : 0)
                            && view.ActualHeight > 0 && double.IsFinite(view.ActualHeight),
                            $"{mode}/{kind}/{element.Id}: standalone layout", checks);
                        // Render executes the custom charts and gauges, catching drawing-time layout failures.
                        Render(view, 1);
                    }

                    var all = new AppSettings();
                    var allProfile = all.DisplayProfiles.For(mode);
                    foreach (var pair in allProfile.Items) pair.Value.Visible = pair.Key == kind;
                    allProfile.Items[kind].Elements = DisplayCatalog.Elements(kind).Select(x => x.Id).ToHashSet();
                    WidgetView full = mode == WidgetMode.Compact ? new CompactWidgetView() : new ExpandedWidgetView();
                    full.Refresh(all, snapshot, history);
                    Layout(full, mode == WidgetMode.Compact ? 230 : 310);
                    var previousHeight = full.ActualHeight;
                    full.Refresh(all, snapshot, history);
                    Layout(full, mode == WidgetMode.Compact ? 230 : 310);
                    Assert(Math.Abs(previousHeight - full.ActualHeight) < .1,
                        $"{mode}/{kind}: combined elements retain layout between samples", checks);
                }
            }

            controller.Settings.DisplayProfiles = new DisplayProfiles();
            controller.Settings.WidgetMode = WidgetMode.Compact;
            controller.Save();
            await FlushLayout();
            settingsWindow = new SettingsWindow(controller);
            settingsWindow.Show();
            settingsWindow.ProfileTabs.SelectedIndex = 0;
            await FlushLayout();
            var compactTab = (TabItem)settingsWindow.ProfileTabs.Items[0];
            var compactContent = (DependencyObject)compactTab.Content;
            var cpuControls = CpuChoices(compactContent);
            var gauge = cpuControls.Single(c => Equals(c.Tag, ElementIds.Gauge));
            var usage = cpuControls.Single(c => Equals(c.Tag, ElementIds.Usage));
            var graph = cpuControls.Single(c => Equals(c.Tag, ElementIds.Graph));
            Assert(usage.Template.FindName("CheckMark", usage) is System.Windows.Shapes.Path { Visibility: Visibility.Visible }
                && graph.Template.FindName("CheckMark", graph) is System.Windows.Shapes.Path { Visibility: Visibility.Collapsed }
                && usage.Template.FindName("CheckSurface", usage) is Border { Background: SolidColorBrush checkedFill }
                && graph.Template.FindName("CheckSurface", graph) is Border { Background: SolidColorBrush uncheckedFill }
                && checkedFill.Color != uncheckedFill.Color,
                "Checked and unchecked settings controls have distinct glyphs and contrasting fills", checks);
            Click(gauge, false);
            Assert(!controller.Settings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Has(ElementIds.Gauge),
                "Settings checkbox Click updates the compact profile immediately", checks);
            var expandedBefore = Fingerprint(controller.Settings.DisplayProfiles.Expanded);
            Click(usage, false);
            Assert(usage.IsChecked == true && controller.Settings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Has(ElementIds.Usage),
                "Last visible element cannot be unchecked", checks);
            Click(graph, true);
            Click(usage, false);
            Assert(controller.Settings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Elements.SetEquals([ElementIds.Graph]),
                "Settings supports a graph without usage numbers", checks);
            Assert(Fingerprint(controller.Settings.DisplayProfiles.Expanded) == expandedBefore,
                "Compact settings do not modify expanded settings", checks);

            var visible = Descendants<CheckBox>(compactContent).Single(c => c.Name == "VisibleCpu");
            Click(visible, false);
            Assert(!graph.IsEnabled && controller.Settings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Elements.SetEquals([ElementIds.Graph]),
                "Hiding a metric disables controls and preserves selected elements", checks);
            Assert(graph.Template.FindName("CheckMark", graph) is System.Windows.Shapes.Path { Visibility: Visibility.Visible }
                && graph.Template.FindName("CheckRoot", graph) is Grid { Opacity: < 1 },
                "Disabled selected controls retain their checkmark with a visibly muted state", checks);
            Click(visible, true);
            Assert(graph.IsEnabled && graph.IsChecked == true,
                "Showing a metric restores selected elements", checks);

            var restore = Descendants<Button>(compactContent).Single(b => Equals(b.Content, "이 모드 기본값 복원"));
            restore.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(Fingerprint(controller.Settings.DisplayProfiles.Compact) == Fingerprint(DisplayCatalog.CreateDefaults(WidgetMode.Compact))
                && Fingerprint(controller.Settings.DisplayProfiles.Expanded) == expandedBefore,
                "Restore defaults affects only the edited mode", checks);
            await FlushLayout();

            // UI Automation Toggle does not raise ButtonBase.Click: exercise the actual accessibility
            // contract separately from pointer-style events so model and visuals cannot drift apart.
            compactContent = (DependencyObject)((TabItem)settingsWindow.ProfileTabs.Items[0]).Content;
            cpuControls = CpuChoices(compactContent);
            usage = cpuControls.Single(c => Equals(c.Tag, ElementIds.Usage));
            gauge = cpuControls.Single(c => Equals(c.Tag, ElementIds.Gauge));
            graph = cpuControls.Single(c => Equals(c.Tag, ElementIds.Graph));
            ToggleWithAutomation(graph);
            Assert(graph.IsChecked == true && controller.Settings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Has(ElementIds.Graph),
                "UIA TogglePattern enables CPU graph in both the checkbox and persisted model", checks);
            ToggleWithAutomation(usage);
            ToggleWithAutomation(gauge);
            ToggleWithAutomation(graph);
            Assert(graph.IsChecked == true && controller.Settings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Elements.SetEquals([ElementIds.Graph]),
                "UIA TogglePattern cannot disable the final visible display element", checks);
            visible = Descendants<CheckBox>(compactContent).Single(c => c.Name == "VisibleCpu");
            ToggleWithAutomation(visible);
            Assert(visible.IsChecked == false && !controller.Settings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Visible
                && !graph.IsEnabled && controller.Settings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Elements.SetEquals([ElementIds.Graph]),
                "UIA TogglePattern hides CPU and preserves the disabled graph selection", checks);
            ToggleWithAutomation(visible);
            Assert(visible.IsChecked == true && controller.Settings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Visible
                && graph.IsEnabled && graph.IsChecked == true,
                "UIA TogglePattern restores CPU visibility and its display selection", checks);
            Assert(Fingerprint(controller.Settings.DisplayProfiles.Expanded) == expandedBefore,
                "UIA changes leave the other display profile untouched", checks);
            var launchArguments = Environment.GetCommandLineArgs();
            var settingsArgument = Array.IndexOf(launchArguments, "--settings-dir");
            if (settingsArgument >= 0 && settingsArgument + 1 < launchArguments.Length)
            {
                var savedSettingsPath = Path.Combine(launchArguments[settingsArgument + 1], "settings.json");
                var savedSettings = new SettingsStore(savedSettingsPath).Load();
                Assert(File.Exists(savedSettingsPath) && savedSettings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Visible
                    && savedSettings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Elements.SetEquals([ElementIds.Graph]),
                    "UIA changes automatically reach the app settings file without a Click event", checks);
            }
            restore = Descendants<Button>(compactContent).Single(b => Equals(b.Content, "이 모드 기본값 복원"));
            restore.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await FlushLayout();
            Capture((FrameworkElement)settingsWindow.Content, "settings-compact.png");
            settingsWindow.ProfileTabs.SelectedIndex = 1;
            await FlushLayout();
            Capture((FrameworkElement)settingsWindow.Content, "settings-expanded.png");
            settingsWindow.ProfileTabs.SelectedIndex = 2;
            await FlushLayout();
            var generalContent = (DependencyObject)((TabItem)settingsWindow.ProfileTabs.Items[2]).Content;
            var alwaysOnTop = Descendants<CheckBox>(generalContent).Single(c => Equals(c.Content, "항상 위에 표시"));
            var previousAlwaysOnTop = controller.Settings.AlwaysOnTop;
            ToggleWithAutomation(alwaysOnTop);
            Assert(controller.Settings.AlwaysOnTop != previousAlwaysOnTop && window.Topmost == controller.Settings.AlwaysOnTop,
                "UIA general-setting toggle immediately updates the window without recursive events", checks);
            ToggleWithAutomation(alwaysOnTop);
            var transparencyToggle = Descendants<CheckBox>(generalContent).Single(c => Equals(c.Content, "반투명 배경"));
            var transparencySlider = Descendants<Slider>(generalContent).Single(slider => slider.Name == "BackgroundTransparencySlider");
            var transparencyLabel = Descendants<TextBlock>(generalContent).Single(text => text.Name == "BackgroundTransparencyValue");
            var transparencyPeer = UIElementAutomationPeer.CreatePeerForElement(transparencySlider) ?? new SliderAutomationPeer(transparencySlider);
            if (transparencyPeer.GetPattern(PatternInterface.RangeValue) is not IRangeValueProvider transparencyRange)
                throw new InvalidOperationException("Transparency slider must support accessible range changes.");
            var originalTransparency = controller.Settings.Transparency;
            var originalTransparencyValue = controller.Settings.BackgroundTransparency;
            var originalOpacityMode = controller.Settings.WidgetMode;
            var opacityHistory = controller.History.Samples.ToArray();
            if (transparencyToggle.IsChecked != true) ToggleWithAutomation(transparencyToggle);
            Assert(transparencyRange.Minimum == 0 && transparencyRange.Maximum == 100
                && transparencySlider.Template.FindName("PART_Track", transparencySlider) is Track { Thumb: not null },
                "Background transparency exposes a themed accessible 0–100 slider", checks);
            foreach (var value in new[] { 17, 29, 73 }) transparencyRange.SetValue(value);
            Assert(controller.Settings.BackgroundTransparency == 73 && transparencyLabel.Text == "73%",
                "Rapid slider changes immediately preview the latest background transparency", checks);
            if (settingsArgument >= 0 && settingsArgument + 1 < launchArguments.Length)
            {
                var savePath = Path.Combine(launchArguments[settingsArgument + 1], "settings.json");
                Assert(new SettingsStore(savePath).Load().BackgroundTransparency == originalTransparencyValue,
                    "Slider movement coalesces disk writes instead of writing every step", checks);
                await Task.Delay(500);
                Assert(new SettingsStore(savePath).Load().BackgroundTransparency == 73,
                    "The final slider value is automatically saved after the debounce", checks);
            }
            foreach (var sample in new[] { (Percent: 0, Alpha: 255), (Percent: 35, Alpha: 166), (Percent: 70, Alpha: 77), (Percent: 100, Alpha: 1) })
            {
                transparencyRange.SetValue(sample.Percent);
                foreach (var mode in new[] { WidgetMode.Compact, WidgetMode.Expanded })
                {
                    window.SetMode(mode);
                    await FlushLayout();
                    var expectedAlpha = SystemParameters.HighContrast ? 255 : sample.Alpha;
                    Assert(window.AllowsTransparency && window.Opacity == 1 && window.WidgetFrame.Opacity == 1
                        && window.WidgetFrame is Border { Background: SolidColorBrush brush } && brush.Color.A == expectedAlpha
                        && Descendants<TextBlock>(window.WidgetFrame).All(text => text.Opacity == 1),
                        $"{mode} at {sample.Percent}% changes only background alpha while preserving foreground opacity", checks);
                    if (renderingAvailable)
                    {
                        var raw = Render(window.WidgetFrame, 1, includeBackground: false);
                        var pixel = new byte[4];
                        raw.CopyPixels(new Int32Rect(raw.PixelWidth / 2, 3, 1, 1), pixel, 4, 0);
                        Assert(Math.Abs(pixel[3] - expectedAlpha) <= 1,
                            $"{mode} at {sample.Percent}% produces the expected transparent pixels", checks);
                    }
                }
            }
            ToggleWithAutomation(transparencyToggle);
            Assert(!transparencySlider.IsEnabled && controller.Settings.BackgroundTransparency == 100
                && window.WidgetFrame is Border { Background: SolidColorBrush opaqueBrush } && opaqueBrush.Color.A == 255,
                "Turning transparency off restores an opaque background without forgetting the slider value", checks);
            ToggleWithAutomation(transparencyToggle);
            Assert(transparencySlider.IsEnabled && transparencySlider.Value == 100
                && window.WidgetFrame is Border { Background: SolidColorBrush restoredBrush }
                && restoredBrush.Color.A == (SystemParameters.HighContrast ? 255 : 1),
                "Re-enabling transparency restores its chosen strength and retains a draggable background", checks);
            Assert(controller.History.Samples.SequenceEqual(opacityHistory),
                "Adjusting background transparency never resets or restarts sampling history", checks);
            transparencyRange.SetValue(originalTransparencyValue);
            if (transparencyToggle.IsChecked != originalTransparency) ToggleWithAutomation(transparencyToggle);
            window.SetMode(originalOpacityMode);
            controller.Save();
            await FlushLayout();
            Capture((FrameworkElement)settingsWindow.Content, "settings-general.png");
            var combos = Descendants<ComboBox>((DependencyObject)((TabItem)settingsWindow.ProfileTabs.Items[2]).Content)
                .Where(combo => combo.Name is "GpuDeviceSelector" or "NetworkDeviceSelector").ToArray();
            Assert(combos.Length == 2 && combos.All(combo => combo.Items.Count >= 2),
                "Both shared device selectors expose detected demo devices", checks);
            foreach (var combo in combos)
            {
                Assert(combo.Template.FindName("SelectionPresenter", combo) is ContentPresenter automaticSelection
                    && Descendants<TextBlock>(automaticSelection).Any(text => text.Text == "자동 선택"),
                    "Collapsed device selector displays its label instead of the DeviceOption object", checks);
                combo.SelectedIndex = 1;
                await FlushLayout();
                Assert(combo.SelectedItem is DeviceOption selectedDevice
                    && combo.Template.FindName("SelectionPresenter", combo) is ContentPresenter deviceSelection
                    && Descendants<TextBlock>(deviceSelection).Any(text => text.Text == selectedDevice.Name),
                    "Collapsed device selector displays the selected device name", checks);
                combo.IsDropDownOpen = true;
                await FlushLayout();
                Assert(combo.Template.FindName("PART_Popup", combo) is Popup { IsOpen: true },
                    "Dark device selector popup opens using the WPF control template", checks);
                combo.IsDropDownOpen = false;
            }
            Assert(controller.Settings.SelectedGpuId == "demo-gpu" && controller.Settings.SelectedNetworkId == "demo-network",
                "Device selectors update the shared GPU and network selections", checks);

            var themeChoice = Descendants<ComboBox>(generalContent).Single(combo => combo.Name == "ThemeSelector");
            Assert(themeChoice.Items.Count == 3, "Settings exposes exactly dark, light and Windows system themes", checks);
            var themeHistory = controller.History.Samples.ToArray();
            var originalTheme = controller.Settings.Theme;
            var originalMode = controller.Settings.WidgetMode;
            var sharedBackground = Theme.Background;
            var sharedText = Theme.Text;
            foreach (var themeMode in new[] { AppThemeMode.Light, AppThemeMode.Dark, AppThemeMode.System })
            {
                themeChoice.SelectedValue = themeMode;
                await FlushLayout();
                bool expectedDark = themeMode == AppThemeMode.Dark;
                if (themeMode == AppThemeMode.System)
                {
                    using var personalization = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    expectedDark = personalization?.GetValue("AppsUseLightTheme") is int preference && preference == 0;
                }
                Assert(controller.Settings.Theme == themeMode && Theme.IsDark == expectedDark,
                    $"Selecting {themeMode} immediately applies the expected Windows color preference", checks);
                Assert(ReferenceEquals(sharedBackground, Theme.Background) && ReferenceEquals(sharedText, Theme.Text)
                    && !sharedBackground.IsFrozen && !sharedText.IsFrozen
                    && ((SolidColorBrush)settingsWindow.Background).Color == Theme.Background.Color
                    && (Theme.IsDark ? Theme.Text.Color.R > Theme.Background.Color.R : Theme.Text.Color.R < Theme.Background.Color.R),
                    $"{themeMode} updates already-open controls with readable shared colors", checks);
                Theme.RefreshSystemTheme();
                Assert(Theme.IsDark == expectedDark, $"Windows preference refresh respects the {themeMode} selection", checks);
                if (settingsArgument >= 0 && settingsArgument + 1 < launchArguments.Length)
                    Assert(new SettingsStore(Path.Combine(launchArguments[settingsArgument + 1], "settings.json")).Load().Theme == themeMode,
                        $"{themeMode} selection is automatically persisted", checks);
                Capture((FrameworkElement)settingsWindow.Content, $"theme-{themeMode.ToString().ToLowerInvariant()}-settings.png");
                foreach (var mode in new[] { WidgetMode.Expanded, WidgetMode.Compact })
                {
                    window.SetMode(mode);
                    await FlushLayout();
                    Capture(window.WidgetFrame, $"theme-{themeMode.ToString().ToLowerInvariant()}-{mode.ToString().ToLowerInvariant()}.png");
                    if (mode == WidgetMode.Expanded) AssertToolbarBounds(window, checks, 1);
                }
            }
            Assert(controller.History.Samples.SequenceEqual(themeHistory)
                && controller.Settings.SelectedGpuId == "demo-gpu" && controller.Settings.SelectedNetworkId == "demo-network",
                "Changing themes and widget modes preserves samples and selected devices", checks);
            themeChoice.SelectedValue = originalTheme;
            window.SetMode(originalMode);
            await FlushLayout();
            if (!controller.Settings.Transparency) ToggleWithAutomation(transparencyToggle);
            transparencyRange.SetValue(61);
            settingsWindow.Close();
            settingsWindow = null;
            if (settingsArgument >= 0 && settingsArgument + 1 < launchArguments.Length)
                Assert(new SettingsStore(Path.Combine(launchArguments[settingsArgument + 1], "settings.json")).Load().BackgroundTransparency == 61,
                    "Closing settings flushes a pending transparency change before the debounce fires", checks);
            controller.Settings.BackgroundTransparency = originalTransparencyValue;
            controller.Settings.Transparency = originalTransparency;
            controller.Save();

            // Toggle all metric visibility through the real settings handlers, including the empty state.
            settingsWindow = new SettingsWindow(controller);
            settingsWindow.Show();
            settingsWindow.ProfileTabs.SelectedIndex = 0;
            await FlushLayout();
            compactContent = (DependencyObject)((TabItem)settingsWindow.ProfileTabs.Items[0]).Content;
            foreach (var check in Descendants<CheckBox>(compactContent).Where(c => c.Name.StartsWith("Visible", StringComparison.Ordinal)).ToArray()) Click(check, false);
            await FlushLayout();
            Assert(Descendants<Button>(window.WidgetFrame).Any(b => Equals(b.Content, "표시 항목 없음 · 설정 열기"))
                && !Descendants<HistoryChart>(window.WidgetFrame).Any()
                && window.Height < 100,
                "All-hidden compact profile collapses to the settings empty state", checks);
            Capture(window.WidgetFrame, "empty-state.png");
            settingsWindow.Close();
            settingsWindow = null;

            foreach (var options in controller.Settings.DisplayProfiles.Expanded.Items.Values) options.Visible = false;
            window.SetMode(WidgetMode.Expanded);
            await FlushLayout();
            Assert(Descendants<Button>(window.WidgetFrame).Any(b => Equals(b.Content, "표시 항목 없음 · 설정 열기"))
                && !Descendants<HistoryChart>(window.WidgetFrame).Any()
                && window.Height < 100,
                "All-hidden expanded profile also collapses to the settings empty state", checks);

            var persistencePath = Path.Combine(directory, "settings-roundtrip.json");
            var store = new SettingsStore(persistencePath);
            store.Save(controller.Settings);
            var reloaded = store.Load();
            Assert(Fingerprint(reloaded.DisplayProfiles.Compact) == Fingerprint(controller.Settings.DisplayProfiles.Compact)
                && Fingerprint(reloaded.DisplayProfiles.Expanded) == Fingerprint(controller.Settings.DisplayProfiles.Expanded)
                && reloaded.SelectedGpuId == controller.Settings.SelectedGpuId && reloaded.SelectedNetworkId == controller.Settings.SelectedNetworkId,
                "Settings save and reload preserve profiles, visibility and shared device choices", checks);

            controller.Settings.DisplayProfiles = new DisplayProfiles();
            controller.Save();
            var historyBefore = controller.History.Samples.ToArray();
            foreach (var mode in new[] { WidgetMode.Expanded, WidgetMode.Compact })
            {
                window.SetMode(mode);
                await FlushLayout();
                Assert(controller.History.Samples.SequenceEqual(historyBefore),
                    $"Switching to {mode} preserves the collector history", checks);
                foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
                {
                    if (mode == WidgetMode.Expanded) AssertToolbarBounds(window, checks, scale);
                    if (!renderingAvailable) continue;
                    string path = Path.Combine(directory, $"{mode.ToString().ToLowerInvariant()}-{(int)(scale * 100)}.png");
                    SaveScreenshot(window.WidgetFrame, path, scale);
                    using var stream = File.OpenRead(path);
                    var bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                    Assert(bitmap.PixelWidth == (int)Math.Ceiling(window.WidgetFrame.ActualWidth * scale)
                        && bitmap.PixelHeight == (int)Math.Ceiling(window.WidgetFrame.ActualHeight * scale),
                        $"{mode} renders at {scale:P0} bitmap scale (not a system DPI transition)", checks);
                }
            }
            var afterPause = controller.History.Samples.ToArray();
            await Task.Delay(1200);
            Assert(controller.History.Samples.SequenceEqual(afterPause), "Hidden collector does not append samples", checks);

            // A missing reading remains visibly unavailable even when only its chart is selected.
            var unavailableSettings = new AppSettings();
            foreach (var pair in unavailableSettings.DisplayProfiles.Compact.Items) pair.Value.Visible = pair.Key == MetricKind.Cpu;
            unavailableSettings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Elements = [ElementIds.Graph];
            var unavailable = new CompactWidgetView();
            unavailable.Refresh(unavailableSettings, AppController.EmptySnapshot(ReadingState.Unsupported), history);
            Layout(unavailable, 230);
            Assert(Descendants<TextBlock>(unavailable).Any(t => t.Text == "!" && t.Visibility == Visibility.Visible)
                && Descendants<HistoryChart>(unavailable).Count() == 1,
                "Graph-only unsupported readings keep the status indicator", checks);

            await UpdateUiSmoke.RunAsync(checks, directory);
            WriteReport(output, checks, skipped, renderingAvailable, null);
        }
        catch (Exception error)
        {
            WriteReport(output, checks, skipped, renderingAvailable, error.ToString());
            throw;
        }
        finally { settingsWindow?.Close(); }

        void Capture(FrameworkElement element, string name)
        {
            if (renderingAvailable) SaveScreenshot(element, Path.Combine(directory, name), 1);
        }
    }

    public static void SaveScreenshot(FrameworkElement element, string path, double scale)
    {
        if (scale <= 0 || !double.IsFinite(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var bitmap = Render(element, scale);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        if (!Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[i * 4 + 3] != 0))
            throw new InvalidOperationException("The WPF screenshot is empty; rendering did not produce visible pixels.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static RenderTargetBitmap Render(FrameworkElement element, double scale, bool includeBackground = true)
    {
        element.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * scale));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            if (includeBackground)
                drawing.DrawRectangle(Theme.Background, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            drawing.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null,
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static bool CanRenderPixels()
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 16, 16));
        var bitmap = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[16 * 16 * 4];
        bitmap.CopyPixels(pixels, 16 * 4, 0);
        return Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[i * 4 + 3] != 0);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T typed) yield return typed;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static CheckBox[] CpuChoices(DependencyObject content)
    {
        var visible = Descendants<CheckBox>(content).Single(c => c.Name == "VisibleCpu");
        var card = VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(visible));
        return Descendants<CheckBox>(card).Where(c => c.Tag is string).ToArray();
    }

    private static void Click(CheckBox check, bool enabled)
    {
        check.IsChecked = enabled;
        check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static void ToggleWithAutomation(CheckBox check)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(check) ?? new CheckBoxAutomationPeer(check);
        if (peer.GetPattern(PatternInterface.Toggle) is not IToggleProvider toggle)
            throw new InvalidOperationException("The checkbox does not expose UI Automation TogglePattern.");
        toggle.Toggle();
    }

    private static string Fingerprint(DisplayProfile profile) => string.Join("|", profile.Items.OrderBy(x => x.Key)
        .Select(x => $"{x.Key}:{x.Value.Visible}:{string.Join(",", x.Value.Elements.OrderBy(id => id, StringComparer.Ordinal))}"));

    private static void Layout(FrameworkElement element, double width)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
        element.UpdateLayout();
    }

    private static void AssertWidgetSizing(MainWindow window, WidgetMode mode, SystemSnapshot snapshot, HistorySample[] history, List<string> checks)
    {
        var frame = window.WidgetFrame;
        var scroll = window.WidgetScroll;
        var originalDpi = VisualTreeHelper.GetDpi(frame);
        var footer = (FrameworkElement)((Grid)((Border)frame).Child).Children[2];
        var originalHeaderVisibility = window.WidgetHeader.Visibility;
        var originalFooterVisibility = footer.Visibility;
        var view = (WidgetView)scroll.Content;
        var longName = string.Concat(Enumerable.Repeat("Very long device name — 장치 이름 · ", 20));
        var longSnapshot = snapshot with
        {
            Cpu = snapshot.Cpu with { Name = longName },
            Gpu = snapshot.Gpu with { Name = longName },
            Network = snapshot.Network with { Name = longName }
        };
        // This uses real WPF measure/arrange at each layout DPI, without changing the user's display.
        // Detaching the frame avoids conflicting with the native window's actual monitor DPI.
        window.Content = null;
        var host = new Decorator { Child = frame, UseLayoutRounding = true, SnapsToDevicePixels = true };
        try
        {
            foreach (double scale in new[] { 1d, 1.25, 1.5, 1.75, 2 })
            {
                VisualTreeHelper.SetRootDpi(host, new DpiScale(scale, scale));
                foreach (var scenario in new[] { "default", "all elements and long names", "CPU number only", "all hidden" })
                {
                    var settings = new AppSettings();
                    var profile = settings.DisplayProfiles.For(mode);
                    if (scenario == "all elements and long names")
                        foreach (var pair in profile.Items) pair.Value.Elements = DisplayCatalog.Elements(pair.Key).Select(x => x.Id).ToHashSet();
                    if (scenario is "CPU number only" or "all hidden")
                    {
                        foreach (var pair in profile.Items) pair.Value.Visible = scenario == "CPU number only" && pair.Key == MetricKind.Cpu;
                        profile.Items[MetricKind.Cpu].Elements = [ElementIds.Usage];
                    }
                    window.WidgetHeader.Visibility = scenario == "all hidden" ? Visibility.Collapsed : originalHeaderVisibility;
                    footer.Visibility = scenario == "all hidden" ? Visibility.Collapsed : originalFooterVisibility;
                    view.Refresh(settings, longSnapshot, history);
                    host.UpdateLayout();
                    var height = window.MeasureDesiredHeight();
                    host.Measure(new Size(window.Width, height));
                    host.Arrange(new Rect(0, 0, window.Width, height));
                    host.UpdateLayout();
                    Assert(scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible && scroll.ScrollableHeight < .1,
                        $"{mode}/{scenario}: no overflow at {scale:P0} WPF layout DPI (extent {scroll.ExtentHeight:0.###}, viewport {scroll.ViewportHeight:0.###}, height {height:0.###})", checks);
                    if (scenario == "all elements and long names")
                    {
                        // A genuinely constrained viewport still scrolls, then shrinking the
                        // profile in the next scenario must remove the bar and stale offset.
                        host.Measure(new Size(window.Width, 120));
                        host.Arrange(new Rect(0, 0, window.Width, 120));
                        host.UpdateLayout();
                        scroll.ScrollToBottom();
                        host.UpdateLayout();
                        Assert(scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible
                            && scroll.ExtentHeight > scroll.ViewportHeight && scroll.VerticalOffset > 0,
                            $"{mode}: a constrained work area retains usable scrolling at {scale:P0} WPF layout DPI", checks);
                    }
                    if (scenario == "CPU number only")
                        Assert(height < 150 && scroll.VerticalOffset == 0,
                            $"{mode}: reducing content clears surplus space and scroll offset at {scale:P0} WPF layout DPI", checks);
                }
            }
        }
        finally
        {
            host.Child = null;
            VisualTreeHelper.SetRootDpi(frame, originalDpi);
            window.WidgetHeader.Visibility = originalHeaderVisibility;
            footer.Visibility = originalFooterVisibility;
            view.Refresh(new AppSettings(), snapshot, history);
            window.Content = frame;
            window.UpdateLayout();
        }
    }

    private static async Task FlushLayout()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void AssertToolbarBounds(MainWindow window, List<string> checks, double scale)
    {
        var header = window.WidgetHeader;
        var headerBounds = header.TransformToAncestor(window.WidgetFrame).TransformBounds(new Rect(header.RenderSize));
        var buttons = Descendants<Button>(header).ToArray();
        var bounds = buttons.Select(button => button.TransformToAncestor(window.WidgetFrame).TransformBounds(new Rect(button.RenderSize))).ToArray();
        double tolerance = 1 / scale;
        Assert(buttons.Length == 4 && bounds.All(rect => rect.Left >= headerBounds.Left - tolerance
            && rect.Right <= headerBounds.Right + tolerance && rect.Top >= headerBounds.Top - tolerance && rect.Bottom <= headerBounds.Bottom + tolerance)
            && bounds.Zip(bounds.Skip(1), (left, right) => left.Right <= right.Left + tolerance).All(value => value)
            && bounds.All(rect => Math.Abs(rect.Top + rect.Height / 2 - (bounds[0].Top + bounds[0].Height / 2)) <= tolerance)
            && Math.Abs(bounds[^1].Right - headerBounds.Right) <= tolerance,
            $"Expanded toolbar stays inside the header, aligned right without overlap at {scale:P0} geometry scale", checks);
        Assert(buttons.All(button =>
        {
            if (button.Content is not System.Windows.Shapes.Path { ActualWidth: > 0, ActualHeight: > 0 } icon) return false;
            var iconBounds = icon.TransformToAncestor(button).TransformBounds(new Rect(icon.RenderSize));
            var pixelTolerance = 1 / VisualTreeHelper.GetDpi(icon).DpiScaleX;
            return icon.Width == 14 && icon.Height == 14
                // Uniform stretching preserves the narrow pin's aspect ratio.
                && icon.ActualWidth <= 14 + pixelTolerance && icon.ActualHeight <= 14 + pixelTolerance
                && Math.Abs(Math.Max(icon.ActualWidth, icon.ActualHeight) - 14) <= pixelTolerance
                && Math.Abs(iconBounds.Left + iconBounds.Width / 2 - button.ActualWidth / 2) <= pixelTolerance
                && Math.Abs(iconBounds.Top + iconBounds.Height / 2 - button.ActualHeight / 2) <= pixelTolerance;
        }), "Toolbar vector icons fit their shared size and stay centered after device-pixel layout rounding", checks);
    }

    private static void Assert(bool condition, string name, List<string> checks)
    {
        if (!condition) throw new InvalidOperationException("UI smoke assertion failed: " + name);
        checks.Add(name);
    }

    private static void WriteReport(string output, IReadOnlyList<string> checks, IReadOnlyList<string> skipped, bool renderingAvailable, string? error) =>
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            success = error is null,
            testedAt = DateTimeOffset.UtcNow,
            assertionCount = checks.Count,
            assertions = checks,
            renderingAvailable,
            skipped,
            limitations = new[] { "Bitmap scale checks do not change Windows display DPI or simulate monitor removal.",
                "Demo readings exercise UI behavior; native measurement accuracy requires the separate probe." },
            error
        }, new JsonSerializerOptions { WriteIndented = true }));
}
