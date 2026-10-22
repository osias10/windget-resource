using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using ResourceStatus.Core;
using Forms = System.Windows.Forms;

namespace ResourceStatus.App;

/// <summary>Simulates OS window relocation without changing monitors, display power, or system sleep.</summary>
internal static class PlacementUiSmoke
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPosition
    {
        public IntPtr Window, InsertAfter;
        public int Left, Top, Width, Height;
        public uint Flags;
    }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public static async Task RunAsync(List<string> checks, string directory)
    {
        VerifyPowerMessages();
        VerifyPositionMessages();
        using (var fixture = await Fixture.CreateAsync(directory, "display-resume"))
        {
            var window = fixture.Window;
            var preferred = fixture.Preferred();
            Assert(SamePoint(Capture(window), preferred), "Legacy settings with only X/Y coordinates restore to the original location");
            var displaced = Displace(window);
            Assert(!SamePoint(displaced, preferred), "The placement regression simulates a real native OS move within the current work area");
            window.RestorePlacementAfterDisplayChange();
            await RestoreTick();
            Assert(SamePoint(Capture(window), preferred), "Display-change recovery returns an OS-relocated widget to its preferred position");
            Assert(SamePoint(fixture.SavedPreferred(), preferred), "Display recovery does not replace the saved user position with the temporary OS position");

            Displace(window);
            window.ProcessPowerChange(PowerModes.Resume);
            await RestoreTick();
            Assert(SamePoint(Capture(window), preferred), "Resume recovery restores the preferred position in addition to resetting sampling");
            Assert(SamePoint(fixture.SavedPreferred(), preferred), "Resume recovery preserves the original saved coordinates");
            var monitors = DisplayMonitors.GetWorkAreas();
            Assert(WindowPlatform.TryCapture(window, out var anchored)
                && monitors.Any(monitor => monitor.Id == anchored.MonitorId
                    && anchored.MonitorOffsetX == anchored.Left - monitor.Left
                    && anchored.MonitorOffsetY == anchored.Top - monitor.Top),
                "Native position capture includes the monitor identity and offsets from a freshly enumerated work area");
            Displace(window);
            var powerMessage = CreatePowerMessage(new Guid("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5"), 4, 1);
            try { SendMessage(new WindowInteropHelper(window).Handle, 0x0218, new IntPtr(0x8013), powerMessage); }
            finally { Marshal.FreeHGlobal(powerMessage); }
            await RestoreTick();
            Assert(SamePoint(Capture(window), preferred),
                "A display-on message sent only to the widget schedules preferred-position recovery without changing system power");
        }

        using (var fixture = await Fixture.CreateAsync(directory, "late-os-move"))
        {
            var preferred = fixture.Preferred();
            var handle = new WindowInteropHelper(fixture.Window).Handle;
            // End any startup recovery sequence before simulating a later Windows move.
            // No display, resume, or power notification is sent after the displacement.
            SendMessage(handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
            SendMessage(handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
            await Flush();
            Assert(!fixture.Window.IsPlacementRecoveryPending,
                "The late-OS-move regression begins after earlier position recovery retries are canceled");
            Displace(fixture.Window);
            await RestoreTick();
            Assert(SamePoint(Capture(fixture.Window), preferred) && SamePoint(fixture.SavedPreferred(), preferred),
                "A late OS move restores the preferred position without requiring another display or resume event");
            await Task.Delay(TimeSpan.FromSeconds(8));
            await Flush();
            Assert(!fixture.Window.IsPlacementRecoveryPending && SamePoint(Capture(fixture.Window), preferred),
                "Position recovery stops after its bounded retries and does not reschedule itself through its own native moves");
        }

        using (var fixture = await Fixture.CreateAsync(directory, "mode-and-close"))
        {
            var compact = fixture.Preferred();
            Displace(fixture.Window);
            fixture.Window.SetMode(WidgetMode.Expanded);
            await Flush();
            Assert(SamePoint(fixture.SavedPreferred(WidgetMode.Compact), compact),
                "Switching modes after an OS move does not overwrite the compact preferred position");
            Assert(SamePoint(Capture(fixture.Window), compact),
                "A mode without its own position inherits the other mode's preferred position, not its temporary OS position");
            fixture.Window.SetMode(WidgetMode.Compact);
            await Flush();
            Assert(SamePoint(Capture(fixture.Window), compact), "Switching back returns to the compact preferred position");
            Displace(fixture.Window);
            fixture.CloseWindow();
            Assert(SamePoint(fixture.SavedPreferred(), compact), "Closing an OS-relocated widget preserves its original user position on disk");
        }

        using (var fixture = await Fixture.CreateAsync(directory, "hidden-restore"))
        {
            var preferred = fixture.Preferred();
            fixture.Window.Hide();
            Displace(fixture.Window);
            fixture.Window.RestorePlacementAfterDisplayChange();
            fixture.Window.ProcessPowerChange(PowerModes.Resume);
            await RestoreTick();
            Assert(!fixture.Window.IsVisible, "Display and resume recovery never show a widget hidden in the tray");
            Assert(SamePoint(fixture.SavedPreferred(), preferred), "Recovery while hidden leaves the saved preferred position unchanged");
            fixture.Window.ShowWidget();
            await RestoreTick();
            Assert(fixture.Window.IsVisible && SamePoint(Capture(fixture.Window), preferred),
                "Restoring a hidden widget uses the preferred position even after Windows moved its hidden HWND");
        }

        using (var fixture = await Fixture.CreateAsync(directory, "user-move"))
        {
            var original = fixture.Preferred();
            Displace(fixture.Window);
            fixture.Window.RestorePlacementAfterDisplayChange();
            var userPosition = Capture(fixture.Window);
            fixture.Window.SavePlacement(); // Explicit user movement commit; no display settings are changed.
            var expected = JsonSerializer.Serialize(fixture.Controller.Settings.Placements[WidgetMode.Compact]);
            Assert(SamePoint(fixture.SavedPreferred(), userPosition)
                && JsonSerializer.Serialize(fixture.Store.Load().Placements[WidgetMode.Compact]) == expected,
                "An explicit user move saves its new coordinates and monitor anchor metadata together");
            await RestoreTick();
            Assert(SamePoint(Capture(fixture.Window), userPosition) && !SamePoint(userPosition, original),
                "A pending display restore cannot undo a later explicit user movement");

            var noOpStart = Displace(fixture.Window);
            fixture.Window.CommitUserMove(noOpStart);
            Assert(SamePoint(fixture.SavedPreferred(), userPosition),
                "Ending a drag at its starting coordinates cannot save a temporary OS position over the user's anchor");
            var handle = new WindowInteropHelper(fixture.Window).Handle;
            SendMessage(handle, 0x0231, IntPtr.Zero, IntPtr.Zero); // WM_ENTERSIZEMOVE
            SendMessage(handle, 0x0232, IntPtr.Zero, IntPtr.Zero); // WM_EXITSIZEMOVE, no displacement within the loop
            await RestoreTick();
            Assert(SamePoint(fixture.SavedPreferred(), userPosition),
                "A move loop with no movement cannot persist an existing temporary OS relocation as a user preference");
        }

        using (var fixture = await Fixture.CreateAsync(directory, "first-placement", withSavedPosition: false))
        {
            Assert(fixture.Store.Load().Placements.TryGetValue(WidgetMode.Compact, out var first)
                && SamePoint(first, Capture(fixture.Window)),
                "First startup saves a preferred position before any display interruption or manual drag");
        }

        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Placement UI assertion failed: " + message);
            checks.Add(message);
        }

        void VerifyPowerMessages()
        {
            var sessionDisplay = new Guid("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
            var data = CreatePowerMessage(sessionDisplay, 4, 1);
            try
            {
                Assert(WindowPlatform.IsDisplayRestoredMessage(0x0218, new IntPtr(0x8013), data),
                    "The session display-on notification is recognized");
                Marshal.WriteInt32(data, 20, 2);
                Assert(WindowPlatform.IsDisplayRestoredMessage(0x0218, new IntPtr(0x8013), data),
                    "A dimmed but available session display is recognized as restored");
                Marshal.WriteInt32(data, 20, 0);
                Assert(!WindowPlatform.IsDisplayRestoredMessage(0x0218, new IntPtr(0x8013), data),
                    "Turning a display off does not trigger display-on recovery");
                Marshal.WriteInt32(data, 20, 1);
                Marshal.WriteInt32(data, 16, 0);
                Assert(!WindowPlatform.IsDisplayRestoredMessage(0x0218, new IntPtr(0x8013), data),
                    "Malformed power notification lengths are rejected before reading their payload");
                Marshal.WriteInt32(data, 16, 4);
                Marshal.StructureToPtr(Guid.Empty, data, false);
                Assert(!WindowPlatform.IsDisplayRestoredMessage(0x0218, new IntPtr(0x8013), data)
                    && !WindowPlatform.IsDisplayRestoredMessage(0x0218, new IntPtr(0x8013), IntPtr.Zero)
                    && !WindowPlatform.IsDisplayRestoredMessage(0x001A, new IntPtr(0x8013), data),
                    "Unrelated or absent power notifications cannot trigger display recovery");
            }
            finally { Marshal.FreeHGlobal(data); }
        }

        void VerifyPositionMessages()
        {
            var data = Marshal.AllocHGlobal(Marshal.SizeOf<NativeWindowPosition>());
            try
            {
                var position = new NativeWindowPosition
                {
                    Window = IntPtr.Zero, InsertAfter = IntPtr.Zero,
                    Left = 0, Top = 0, Width = 0, Height = 0, Flags = 0
                };
                Marshal.StructureToPtr(position, data, false);
                Assert(WindowPlatform.WasWindowMoved(data), "A native WINDOWPOS location change is recognized");
                position.Flags = 0x0002; // SWP_NOMOVE
                Marshal.StructureToPtr(position, data, false);
                Assert(!WindowPlatform.WasWindowMoved(data) && !WindowPlatform.WasWindowMoved(IntPtr.Zero),
                    "Native notifications that cannot move the window do not trigger placement recovery");
            }
            finally { Marshal.FreeHGlobal(data); }
        }
    }

    private static IntPtr CreatePowerMessage(Guid setting, int length, int state)
    {
        var data = Marshal.AllocHGlobal(24);
        Marshal.StructureToPtr(setting, data, false);
        Marshal.WriteInt32(data, 16, length);
        Marshal.WriteInt32(data, 20, state);
        return data;
    }

    private static WindowPlacement Capture(MainWindow window)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out var rect)) throw new InvalidOperationException("GetWindowRect failed.");
        return new WindowPlacement { Left = rect.Left, Top = rect.Top };
    }

    private static WindowPlacement Displace(MainWindow window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(handle, out var rect)) throw new InvalidOperationException("GetWindowRect failed.");
        var work = Forms.Screen.FromHandle(handle).WorkingArea;
        int maxX = Math.Max(work.Left, work.Right - (rect.Right - rect.Left));
        int maxY = Math.Max(work.Top, work.Bottom - (rect.Bottom - rect.Top));
        int x = Math.Clamp(rect.Left + 90, work.Left, maxX), y = Math.Clamp(rect.Top + 70, work.Top, maxY);
        if (x == rect.Left && y == rect.Top)
        {
            x = Math.Clamp(rect.Left - 90, work.Left, maxX);
            y = Math.Clamp(rect.Top - 70, work.Top, maxY);
        }
        if (!SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010))
            throw new InvalidOperationException("Simulated OS window relocation failed.");
        return Capture(window);
    }

    private static bool SamePoint(WindowPlacement? left, WindowPlacement? right) => left is not null && right is not null
        && Math.Abs(left.Left - right.Left) <= 1 && Math.Abs(left.Top - right.Top) <= 1;
    private static async Task Flush() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static async Task RestoreTick() { await Task.Delay(650); await Flush(); }

    private sealed class Fixture : IDisposable
    {
        public required SettingsStore Store { get; init; }
        public required AppController Controller { get; init; }
        public required MainWindow Window { get; init; }
        private bool _closed;

        public static async Task<Fixture> CreateAsync(string directory, string scenario, bool withSavedPosition = true)
        {
            var path = Path.Combine(directory, "placement-" + scenario + ".json");
            var store = new SettingsStore(path);
            var settings = new AppSettings();
            foreach (var profile in new[] { settings.DisplayProfiles.Compact, settings.DisplayProfiles.Expanded })
            {
                foreach (var pair in profile.Items) pair.Value.Visible = pair.Key == MetricKind.Cpu;
                profile.Items[MetricKind.Cpu].Elements = [ElementIds.Usage];
            }
            if (withSavedPosition)
            {
                var work = Forms.Screen.PrimaryScreen!.WorkingArea;
                settings.Placements[WidgetMode.Compact] = new WindowPlacement { Left = work.Left + 40, Top = work.Top + 40 };
            }
            store.Save(settings);
            var controller = new AppController(path, demo: true);
            var window = new MainWindow(controller) { AllowClose = true };
            var fixture = new Fixture { Store = store, Controller = controller, Window = window };
            try
            {
                window.Show();
                await Flush();
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        public WindowPlacement Preferred(WidgetMode mode = WidgetMode.Compact) => Controller.Settings.Placements[mode];
        public WindowPlacement SavedPreferred(WidgetMode mode = WidgetMode.Compact) => Store.Load().Placements[mode];
        public void CloseWindow() { if (!_closed) { _closed = true; Window.Close(); } }
        public void Dispose() { CloseWindow(); Controller.Dispose(); }
    }
}
