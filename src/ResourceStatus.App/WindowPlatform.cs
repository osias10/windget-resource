using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using ResourceStatus.Core;
using Forms = System.Windows.Forms;

namespace ResourceStatus.App;

internal static class WindowPlatform
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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

    public static WindowPlacement Capture(Window window)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle, out var rect);
        return new() { Left = rect.Left, Top = rect.Top }; // Physical screen pixels, supports negative monitor origins.
    }

    public static double FitAndPlace(Window window, double desiredHeight, WindowPlacement? position = null, bool initial = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return desiredHeight;
        GetWindowRect(hwnd, out var current);
        var desiredX = (int)(position?.Left ?? current.Left);
        var desiredY = (int)(position?.Top ?? current.Top);
        var screen = initial && position is null ? Forms.Screen.PrimaryScreen! : Forms.Screen.FromPoint(new System.Drawing.Point(desiredX, desiredY));
        var work = screen.WorkingArea;
        double scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96d;
        double height = Math.Min(desiredHeight, Math.Max(40, (work.Height - 16) / scale));
        int widthPx = (int)Math.Ceiling(window.Width * scale), heightPx = (int)Math.Ceiling(height * scale);
        if (initial && position is null) { desiredX = work.Right - widthPx - 24; desiredY = work.Top + 24; }
        desiredX = Math.Clamp(desiredX, work.Left, Math.Max(work.Left, work.Right - widthPx));
        desiredY = Math.Clamp(desiredY, work.Top, Math.Max(work.Top, work.Bottom - heightPx));
        window.Height = height;
        GetWindowRect(hwnd, out var after); // Re-read: the Height assignment may already have resized the HWND.
        if (after.Left == desiredX && after.Top == desiredY && after.Right - after.Left == widthPx && after.Bottom - after.Top == heightPx)
            return height;
        SetWindowPos(hwnd, IntPtr.Zero, desiredX, desiredY, widthPx, heightPx, 0x0010 | 0x0004);
        return height;
    }
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
