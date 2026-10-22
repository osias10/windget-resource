using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using ResourceStatus.Core;

namespace ResourceStatus.App;

internal static class WindowPlatform
{
    private const long ClickThroughStyles = 0x00000020 | 0x08000000; // WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
    private sealed class ClickThroughState { public long? OriginalBits; }
    private static readonly ConditionalWeakTable<Window, ClickThroughState> ClickThroughStates = new();
    private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr notification);

    public static void Initialize(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var style = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, new IntPtr((style | 0x80) & ~0x40000));
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;
        if (!window.AllowsTransparency)
        {
            ApplyTheme(window, Theme.IsDark);
            int corners = 2;
            DwmSetWindowAttribute(hwnd, 33, ref corners, sizeof(int));
        }
    }

    public static void ApplyTheme(Window window, bool isDark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || window.AllowsTransparency) return;
        int dark = isDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    public static void ApplyClickThrough(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var state = ClickThroughStates.GetValue(window, _ => new ClickThroughState());
        var current = GetWindowLongPtr(hwnd, -20).ToInt64();
        long updated;
        if (enabled)
        {
            state.OriginalBits ??= current & ClickThroughStyles;
            updated = current | ClickThroughStyles;
        }
        else
        {
            if (state.OriginalBits is not { } original) return;
            updated = (current & ~ClickThroughStyles) | original;
            state.OriginalBits = null;
        }
        if (updated == current) return;
        SetWindowLongPtr(hwnd, -20, new IntPtr(updated));
        // Refresh cached style information without moving, resizing, reordering or activating.
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
    }

    public static WindowPlacement Capture(Window window)
    {
        if (TryCapture(window, out var placement)) return placement;
        throw new Win32Exception("현재 위젯 위치와 모니터 정보를 읽을 수 없습니다.");
    }

    public static bool TryCapture(Window window, out WindowPlacement placement)
    {
        placement = new();
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || window.WindowState == WindowState.Minimized ||
            !GetWindowRect(hwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top ||
            !DisplayMonitors.TryGetWindowMonitor(hwnd, out var monitor)) return false;
        placement = new()
        {
            Left = rect.Left, Top = rect.Top, MonitorId = monitor.Id,
            MonitorOffsetX = (double)rect.Left - monitor.Left,
            MonitorOffsetY = (double)rect.Top - monitor.Top
        };
        return true;
    }

    public static double FitAndPlace(Window window, double desiredHeight, WindowPlacement? position = null, bool initial = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return desiredHeight;
        if (!GetWindowRect(hwnd, out var current)) return desiredHeight;
        var monitors = DisplayMonitors.GetWorkAreas();
        if (monitors.Count == 0) return desiredHeight;
        var preferred = position ?? new WindowPlacement { Left = current.Left, Top = current.Top };
        var monitor = initial && position is null ? monitors.FirstOrDefault(item => item.IsPrimary) ?? monitors[0]
            : PlacementPolicy.SelectMonitor(preferred, monitors);
        double scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96d;
        double height = Math.Min(desiredHeight, Math.Max(40, (monitor.Height - 16) / scale));
        int widthPx = (int)Math.Ceiling(window.Width * scale), heightPx = (int)Math.Ceiling(height * scale);
        if (initial && position is null)
            preferred = new WindowPlacement
            {
                Left = (double)monitor.Left + monitor.Width - widthPx - 24, Top = (double)monitor.Top + 24,
                MonitorId = monitor.Id, MonitorOffsetX = monitor.Width - widthPx - 24, MonitorOffsetY = 24
            };
        var resolved = PlacementPolicy.Resolve(preferred, monitors, widthPx, heightPx);
        int desiredX = resolved.Left, desiredY = resolved.Top;
        window.Height = height;
        if (!GetWindowRect(hwnd, out var after)) return height; // Height may already have resized the HWND.
        if (after.Left == desiredX && after.Top == desiredY && after.Right - after.Left == widthPx && after.Bottom - after.Top == heightPx)
            return height;
        SetWindowPos(hwnd, IntPtr.Zero, desiredX, desiredY, widthPx, heightPx, 0x0010 | 0x0004);
        return height;
    }

    public static IntPtr RegisterDisplayPowerNotification(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        var setting = SessionDisplayStatus;
        return RegisterPowerSettingNotification(hwnd, ref setting, 0); // DEVICE_NOTIFY_WINDOW_HANDLE
    }

    public static void UnregisterDisplayPowerNotification(IntPtr notification)
    {
        if (notification != IntPtr.Zero) UnregisterPowerSettingNotification(notification);
    }

    public static bool IsDisplayRestoredMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        if (message != 0x0218 || wParam.ToInt64() != 0x8013 || lParam == IntPtr.Zero) return false;
        // POWERBROADCAST_SETTING = GUID (16 bytes), DWORD DataLength, DWORD state.
        if (Marshal.PtrToStructure<Guid>(lParam) != SessionDisplayStatus || Marshal.ReadInt32(lParam, 16) != 4) return false;
        int state = Marshal.ReadInt32(lParam, 20);
        return state is 1 or 2; // PowerMonitorOn / PowerMonitorDim
    }

    public static bool WasWindowMoved(IntPtr windowPosition) => windowPosition != IntPtr.Zero &&
        (Marshal.PtrToStructure<WindowPosition>(windowPosition).Flags & 0x0002) == 0; // SWP_NOMOVE
}

internal static class StartupRegistration
{
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
            key.SetValue("ResourceStatus", "\"" + Environment.ProcessPath + "\"");
        else key.DeleteValue("ResourceStatus", false);
    }
}
