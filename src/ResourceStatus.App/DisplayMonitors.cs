using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ResourceStatus.Core;

namespace ResourceStatus.App;

/// <summary>Reads a fresh monitor snapshot without using WinForms' cached display topology.</summary>
internal static class DisplayMonitors
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr deviceContext, IntPtr rectangle, IntPtr data);
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    internal static IReadOnlyList<MonitorWorkArea> GetWorkAreas()
    {
        var monitors = new List<MonitorWorkArea>();
        bool complete = true;
        bool success = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            if (!TryRead(handle, out var monitor)) { complete = false; return false; }
            monitors.Add(monitor);
            return true;
        }, IntPtr.Zero);
        // A failed/incomplete enumeration is transient during topology changes. The
        // caller leaves the window alone and can retry without replacing its anchor.
        return success && complete ? monitors : [];
    }

    internal static bool TryGetWindowMonitor(IntPtr window, out MonitorWorkArea monitor) =>
        TryRead(MonitorFromWindow(window, 2), out monitor); // MONITOR_DEFAULTTONEAREST

    private static bool TryRead(IntPtr handle, out MonitorWorkArea monitor)
    {
        monitor = null!;
        if (handle == IntPtr.Zero) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(handle, ref info)) return false;
        var work = info.Work;
        long width = (long)work.Right - work.Left, height = (long)work.Bottom - work.Top;
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue) return false;
        string id = GetIdentity(info.DeviceName);
        monitor = new(id, work.Left, work.Top, (int)width, (int)height, (info.Flags & 1) != 0);
        return true;
    }

    private static string GetIdentity(string deviceName)
    {
        var ids = new List<string>();
        // EDD_GET_DEVICE_INTERFACE_NAME returns the per-monitor interface path in
        // DeviceId. GDI DISPLAYn names identify a current view and may be reassigned.
        for (uint index = 0; index < 32; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(deviceName, index, ref device, 1)) break;
            if ((device.StateFlags & 1) != 0 && !string.IsNullOrWhiteSpace(device.DeviceId))
                ids.Add(device.DeviceId.TrimEnd('\0'));
        }
        // Mirrored outputs share a GDI view; deterministic ordering avoids switching
        // identity merely because the native enumeration order changed.
        return ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "gdi:" + deviceName;
    }
}
