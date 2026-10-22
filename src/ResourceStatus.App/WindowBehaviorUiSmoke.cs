using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ResourceStatus.Core;

namespace ResourceStatus.App;

/// <summary>Exercises saved size locks and native pointer behavior using an isolated widget.</summary>
internal static class WindowBehaviorUiSmoke
{
    private const long ClickThroughMask = 0x00000020 | 0x08000000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public static async Task RunAsync(List<string> checks, string directory)
    {
        string path = Path.Combine(directory, "window-behavior-settings.json");
        var store = new SettingsStore(path);
        var initial = new AppSettings();
        SetOnlyCpu(initial.DisplayProfiles.Compact);
        SetOnlyCpu(initial.DisplayProfiles.Expanded);
        // A new lock must capture the visible size, rather than reuse an earlier lock session.
        initial.LockedHeights[WidgetMode.Compact] = 500;
        initial.LockedHeights[WidgetMode.Expanded] = 500;
        store.Save(initial);
        double savedCompactHeight;
        using (var controller = new AppController(path, demo: true))
        {
            var window = new MainWindow(controller) { AllowClose = true };
            int saveDepth = 0, deepestSave = 0;
            controller.SettingsSaving += (_, _) => { saveDepth++; deepestSave = Math.Max(deepestSave, saveDepth); };
            controller.SettingsChanged += (_, _) => saveDepth--;
            try
            {
                window.Show();
                await Flush();
                var compactHeight = window.ActualHeight;
                controller.Settings.LockWidgetSize = true;
                controller.Save();
                var saved = store.Load();
                Assert(saved.LockWidgetSize && saved.LockedHeights.Count == 1
                    && Near(saved.LockedHeights[WidgetMode.Compact], compactHeight),
                    "Enabling size lock saves the current mode's visible height in the same settings write");

                ShowAll(controller.Settings.DisplayProfiles.Compact);
                controller.Save();
                await Flush();
                Assert(Near(window.ActualHeight, compactHeight) && window.Width == 240
                    && window.WidgetScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible
                    && window.WidgetScroll.ScrollableHeight > 0,
                    "Adding compact display elements preserves the locked size and enables necessary scrolling");
                window.WidgetScroll.ScrollToBottom();
                await Flush();
                foreach (var item in controller.Settings.DisplayProfiles.Compact.Items.Values) item.Visible = false;
                controller.Save();
                await Flush();
                Assert(Near(window.ActualHeight, compactHeight)
                    && window.WidgetScroll.ComputedVerticalScrollBarVisibility != Visibility.Visible
                    && window.WidgetScroll.VerticalOffset == 0,
                    "Hiding every metric keeps the locked height and removes an unnecessary scrollbar and offset");

                window.SetMode(WidgetMode.Expanded);
                await Flush();
                var expandedHeight = window.ActualHeight;
                saved = store.Load();
                Assert(window.Width == 320 && saved.LockedHeights.Count == 2
                    && Near(saved.LockedHeights[WidgetMode.Compact], compactHeight)
                    && Near(saved.LockedHeights[WidgetMode.Expanded], expandedHeight),
                    "First switching to the expanded mode captures and persists its independent locked height");
                ShowAll(controller.Settings.DisplayProfiles.Expanded);
                controller.Save();
                await Flush();
                Assert(Near(window.ActualHeight, expandedHeight) && window.WidgetScroll.ScrollableHeight > 0,
                    "The expanded mode also keeps its own locked height when its content grows");
                window.SetMode(WidgetMode.Compact);
                await Flush();
                Assert(Near(window.ActualHeight, compactHeight) && window.Width == 240,
                    "Switching back restores the compact mode's previously locked size");

                controller.Settings.LockWidgetSize = false;
                controller.Save();
                await Flush();
                Assert(window.ActualHeight < compactHeight && window.WidgetScroll.ScrollableHeight == 0,
                    "Turning size lock off immediately restores automatic content height");
                ShowAll(controller.Settings.DisplayProfiles.Compact);
                controller.Save();
                await Flush();
                savedCompactHeight = window.ActualHeight;
                controller.Settings.LockWidgetSize = true;
                controller.Save();
                Assert(controller.Settings.LockedHeights.Count == 1
                    && Near(controller.Settings.LockedHeights[WidgetMode.Compact], savedCompactHeight)
                    && savedCompactHeight > compactHeight,
                    "Re-enabling size lock captures the new current size instead of an obsolete locked height");
                Assert(deepestSave == 1 && saveDepth == 0,
                    "Capturing initial and per-mode fixed heights does not recursively save settings");

                var before = Style(window);
                controller.Settings.ClickThrough = true;
                controller.Save();
                await Flush();
                var enabled = Style(window);
                Assert((enabled & ClickThroughMask) == ClickThroughMask
                    && (enabled & ~ClickThroughMask) == (before & ~ClickThroughMask)
                    && (enabled & 0x00080000) != 0 && !window.ShowActivated,
                    "Click-through adds transparent and no-activate native styles while preserving layered and other window styles");
                Assert(SendMessage(new WindowInteropHelper(window).Handle, 0x0021, IntPtr.Zero, IntPtr.Zero).ToInt64() == 3,
                    "Click-through refuses mouse activation including Windows active-window tracking");
                controller.Save();
                Assert(Style(window) == enabled, "Repeated preference application keeps click-through native styles stable");
                var settingsWindow = new SettingsWindow(controller);
                try
                {
                    settingsWindow.Show();
                    await Flush();
                    Assert((Style(settingsWindow) & ClickThroughMask) == 0,
                        "Click-through on the widget leaves its settings window interactive");
                }
                finally { settingsWindow.Close(); }
                controller.Settings.ClickThrough = false;
                controller.Save();
                await Flush();
                Assert(Style(window) == before && window.ShowActivated,
                    "Disabling click-through restores the original interactive native styles");
                controller.Settings.ClickThrough = true;
                controller.Save();
                Assert(store.Load().ClickThrough, "Click-through is persisted for the next application launch");
            }
            finally { window.Close(); }
        }

        using (var restarted = new AppController(path, demo: true))
        {
            var restoredWindow = new MainWindow(restarted) { AllowClose = true };
            try
            {
                restoredWindow.Show();
                await Flush();
                Assert(Near(restoredWindow.ActualHeight, savedCompactHeight)
                    && (Style(restoredWindow) & ClickThroughMask) == ClickThroughMask && !restoredWindow.ShowActivated,
                    "A newly created widget restores its fixed height and click-through before first display");
                restoredWindow.Hide();
                restoredWindow.ShowWidget();
                await Flush();
                Assert((Style(restoredWindow) & ClickThroughMask) == ClickThroughMask
                    && !restoredWindow.ShowActivated && Near(restoredWindow.ActualHeight, savedCompactHeight),
                    "Tray-style hide and restore preserve click-through without activating or resizing the widget");
                restarted.Settings.ClickThrough = false;
                restarted.Save();
                Assert((Style(restoredWindow) & ClickThroughMask) == 0 && restoredWindow.ShowActivated,
                    "Click-through restored at startup can be disabled to recover normal interaction");
                SetOnlyCpu(restarted.Settings.DisplayProfiles.Compact);
                restarted.Save();
                await Flush();
                Assert(Near(restoredWindow.ActualHeight, savedCompactHeight),
                    "A restored size lock survives content changes after application restart");
                restarted.Settings.LockWidgetSize = false;
                restarted.Save();
                await Flush();
                Assert(restoredWindow.ActualHeight < savedCompactHeight && restoredWindow.WidgetScroll.ScrollableHeight == 0,
                    "Unlocking after restart returns to an automatically fitted widget without a scrollbar");
            }
            finally { restoredWindow.Close(); }
        }

        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Window behavior UI assertion failed: " + message);
            checks.Add(message);
        }
    }

    private static long Style(Window window) => GetWindowLongPtr(new WindowInteropHelper(window).Handle, -20).ToInt64();
    private static bool Near(double left, double right) => Math.Abs(left - right) < 1;
    private static async Task Flush() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private static void SetOnlyCpu(DisplayProfile profile)
    {
        foreach (var pair in profile.Items) pair.Value.Visible = pair.Key == MetricKind.Cpu;
        profile.Items[MetricKind.Cpu].Elements = [ElementIds.Usage];
    }

    private static void ShowAll(DisplayProfile profile)
    {
        foreach (var pair in profile.Items)
        {
            pair.Value.Visible = true;
            pair.Value.Elements = DisplayCatalog.Elements(pair.Key).Select(element => element.Id).ToHashSet();
        }
    }
}
