using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace ResourceStatus.Native;

internal sealed record GpuDevice(string Id, string Luid, string Name, ulong DedicatedBytes);

internal static unsafe class GpuDevices
{
    internal static List<GpuDevice> Enumerate()
    {
        var devices = new List<GpuDevice>();
        ExceptionDispatchInfo? failure = null;
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        nint factory = 0;
        try
        {
            Check(CreateDXGIFactory1(ref iid, out factory));
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12];
            for (uint index = 0; index < 32; index++)
            {
                nint adapter = 0;
                var status = enumerate(factory, index, &adapter);
                if (status == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND
                // One unreadable adapter must not hide the others.
                try
                {
                    Check(status);
                    var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)(*(nint**)adapter)[10];
                    AdapterDescription desc = default;
                    Check(getDescription(adapter, &desc));
                    if ((desc.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE
                    var luid = $"0x{unchecked((uint)desc.LuidHigh):x8}_0x{desc.LuidLow:x8}";
                    // LUID is only stable until reboot. Retain the hardware signature for saved
                    // selections, disambiguating otherwise identical adapters by DXGI order.
                    var signature = $"gpu-{desc.VendorId:x4}-{desc.DeviceId:x4}-{desc.SubSysId:x8}-{desc.Revision:x8}";
                    var ordinal = devices.Count(x => x.Id.StartsWith(signature + "-", StringComparison.Ordinal));
                    devices.Add(new($"{signature}-{ordinal}", luid, new string(desc.Description), desc.DedicatedVideoMemory));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { failure ??= ExceptionDispatchInfo.Capture(ex); }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
        // Report the failure only when it left nothing to show.
        if (devices.Count == 0) failure?.Throw();
        return devices;
    }

    // HRESULTs become COMException (never OutOfMemoryException etc.), keeping DXGI failures recoverable.
    private static void Check(int status)
    {
        if (status < 0) throw new COMException($"DXGI 0x{status:X8}", status);
    }

    internal static void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)instance)[2])(instance);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out nint factory);
}
