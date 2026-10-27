using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceStatus.Core;

namespace ResourceStatus.Native;

internal sealed record DiskDevice(DiskDeviceOption Option, ReadingState State = ReadingState.Ready, string? Detail = null);

/// <summary>Maps stable PnP instance IDs to this boot's physical disk numbers without opening disks for data access.</summary>
internal static class DiskDevices
{
    internal static readonly Guid DiskInterface = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
    internal static readonly Guid VolumeInterface = new("53f5630d-b6bf-11d0-94f2-00a0c91efb8b");
    private const uint StorageDeviceNumber = 0x002d1080, VolumeDiskExtents = 0x00560000;

    internal static List<DiskDevice> Enumerate()
    {
        var guid = DiskInterface;
        var set = SetupDiGetClassDevsW(ref guid, null, 0, 2 | 0x10); // PRESENT | DEVICEINTERFACE
        if (set == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        var devices = new List<DiskDevice>();
        try
        {
            var volumes = VolumeLetters();
            for (uint index = 0; ; index++)
            {
                var data = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref guid, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                SetupDiGetDeviceInterfaceDetailW(set, ref data, 0, 0, out var bytes, ref info);
                if (bytes < 6 || bytes > 1024 * 1024) continue;
                var detail = Marshal.AllocHGlobal(checked((int)bytes));
                try
                {
                    // cbSize includes platform tail padding; DevicePath still starts immediately after the DWORD.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, bytes, out _, ref info)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4);
                    var id = new StringBuilder(512);
                    if (!SetupDiGetDeviceInstanceIdW(set, ref info, id, id.Capacity, out _) || id.Length == 0) continue;
                    var model = Property(set, ref info, 12) ?? Property(set, ref info, 0) ?? "디스크";
                    int? number = null;
                    var state = ReadingState.Ready;
                    string? failure = null;
                    using (var handle = CreateFileW(path!, 0, 1 | 2 | 4, 0, 3, 0, 0))
                    {
                        var output = new byte[12];
                        if (handle.IsInvalid || !DeviceIoControl(handle, StorageDeviceNumber, 0, 0, output,
                            (uint)output.Length, out var received, 0))
                        {
                            var error = Marshal.GetLastWin32Error();
                            state = error == 5 ? ReadingState.AccessDenied : ReadingState.Error;
                            failure = new Win32Exception(error).Message;
                        }
                        else if (received >= 12 && BitConverter.ToUInt32(output, 4) <= int.MaxValue)
                            number = (int)BitConverter.ToUInt32(output, 4);
                        else { state = ReadingState.Error; failure = "디스크 번호를 확인할 수 없습니다."; }
                    }
                    var name = number.HasValue ? $"디스크 {number}" : $"디스크 ({model})";
                    if (number.HasValue && volumes.TryGetValue(number.Value, out var letters)) name += $" ({string.Join(", ", letters)})";
                    var option = new DiskDeviceOption("disk:pnp:" + id, name, model, number);
                    // One physical device may expose multiple interfaces; do not duplicate it.
                    if (!devices.Any(x => x.Option.Id.Equals(option.Id, StringComparison.OrdinalIgnoreCase)))
                        devices.Add(new(option, state, failure));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return devices.OrderBy(x => x.Option.Number ?? int.MaxValue).ThenBy(x => x.Option.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? Property(nint set, ref DeviceInfo info, uint property)
    {
        var buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, buffer, (uint)buffer.Length, out _)) return null;
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim() is { Length: > 0 } text ? text : null;
    }

    private static Dictionary<int, SortedSet<string>> VolumeLetters()
    {
        var result = new Dictionary<int, SortedSet<string>>();
        // Query only local drive letters. A volume may span disks, and a disk may contain many partitions.
        var mask = GetLogicalDrives();
        for (var index = 0; index < 26; index++)
        {
            if ((mask & (1u << index)) == 0) continue;
            var letter = $"{(char)('A' + index)}:";
            var type = GetDriveTypeW(letter + "\\");
            if (type is not (2 or 3)) continue; // removable or fixed, not network/CD-ROM
            using var handle = CreateFileW(@"\\.\" + letter, 0, 1 | 2 | 4, 0, 3, 0, 0);
            if (handle.IsInvalid) continue;
            var buffer = new byte[8 + 24 * 32];
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var success = DeviceIoControl(handle, VolumeDiskExtents, 0, 0, buffer, (uint)buffer.Length, out var bytes, 0);
                var count = BitConverter.ToUInt32(buffer, 0);
                if (!success)
                {
                    if (Marshal.GetLastWin32Error() == 234 && count is > 32 and <= 1024)
                    { buffer = new byte[checked(8 + 24 * (int)count)]; continue; }
                    break;
                }
                if (count > 1024 || 8UL + count * 24UL > bytes) break;
                for (var extent = 0; extent < count; extent++)
                {
                    var disk = BitConverter.ToUInt32(buffer, 8 + 24 * extent);
                    if (disk > int.MaxValue) continue;
                    if (!result.TryGetValue((int)disk, out var letters)) result[(int)disk] = letters = new(StringComparer.OrdinalIgnoreCase);
                    letters.Add(letter);
                }
                break;
            }
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData { public uint Size; public Guid ClassGuid; public uint Flags; public nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public nuint Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint info, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref InterfaceData data, nint detail, uint size, out uint needed, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(nint set, ref DeviceInfo info, StringBuilder id, int size, out uint needed);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(nint set, ref DeviceInfo info, uint property, out uint type, byte[] buffer, uint size, out uint needed);
    [DllImport("setupapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize,
        byte[] output, uint outputSize, out uint received, nint overlapped);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetLogicalDrives();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint GetDriveTypeW(string root);
}

/// <summary>No UI HWND is required. Notifications merely flag a refresh; enumeration stays on the collection thread.</summary>
internal sealed class DiskDeviceNotifications : IDisposable
{
    private readonly NotifyCallback _callback;
    private nint _disk, _volume;
    private int _changes;

    internal DiskDeviceNotifications()
    {
        _callback = Changed;
        Register(DiskDevices.DiskInterface, 1, out _disk);
        Register(DiskDevices.VolumeInterface, 2, out _volume);
    }

    internal int ConsumeChanges() => Interlocked.Exchange(ref _changes, 0);

    private void Register(Guid guid, nint context, out nint registration)
    {
        var filter = new NotifyFilter { Size = 416, ClassGuid = guid }; // 16-byte header + 200-WCHAR union
        if (CM_Register_Notification(ref filter, context, _callback, out registration) != 0) registration = 0;
    }

    private uint Changed(nint notification, nint context, uint action, nint data, uint size)
    {
        Interlocked.Or(ref _changes, (int)context);
        return 0;
    }

    public void Dispose()
    {
        if (_disk != 0) { CM_Unregister_Notification(_disk); _disk = 0; }
        if (_volume != 0) { CM_Unregister_Notification(_volume); _volume = 0; }
        GC.KeepAlive(_callback);
    }

    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct NotifyFilter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public uint FilterType;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint NotifyCallback(nint notification, nint context, uint action, nint data, uint size);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Register_Notification(ref NotifyFilter filter, nint context, NotifyCallback callback, out nint registration);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)] private static extern uint CM_Unregister_Notification(nint registration);
}
