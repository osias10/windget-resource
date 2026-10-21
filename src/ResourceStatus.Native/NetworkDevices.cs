using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ResourceStatus.Native;

internal sealed record NetworkDevice(string Id, string Name, uint Index, ulong Luid, Guid Guid,
    bool Hardware, bool Connected, ulong ReceiveLink, ulong TransmitLink, ulong Received, ulong Sent);

internal static unsafe class NetworkDevices
{
    internal static List<NetworkDevice> Read()
    {
        var status = GetIfTable2(out var table);
        if (status != 0) throw new Win32Exception((int)status);
        try
        {
            var count = *(uint*)table;
            var rows = (IfRow*)(table + 8); // MIB_IF_TABLE2 pads DWORD before the 8-byte-aligned rows.
            var devices = new List<NetworkDevice>();
            for (var i = 0; i < count; i++)
            {
                var row = rows[i];
                if (!Included(in row)) continue;
                var name = new string(row.Alias);
                if (string.IsNullOrWhiteSpace(name)) name = new string(row.Description);
                devices.Add(new(row.InterfaceGuid.ToString("D"), name, row.InterfaceIndex, row.InterfaceLuid,
                    row.InterfaceGuid, (row.Flags & 1) != 0, Connected(in row),
                    NormalizeSpeed(row.ReceiveLinkSpeed), NormalizeSpeed(row.TransmitLinkSpeed), row.InOctets, row.OutOctets));
            }
            return devices;
        }
        finally { FreeMibTable(table); }
    }

    /// <summary>Refreshes one listed adapter by LUID. Null means it was removed or no longer qualifies; rescan.</summary>
    internal static NetworkDevice? ReadEntry(NetworkDevice device)
    {
        IfRow row = default;
        row.InterfaceLuid = device.Luid;
        if (GetIfEntry2(&row) != 0 || !Included(in row) || row.InterfaceGuid != device.Guid) return null;
        return device with
        {
            Connected = Connected(in row), ReceiveLink = NormalizeSpeed(row.ReceiveLinkSpeed),
            TransmitLink = NormalizeSpeed(row.TransmitLinkSpeed), Received = row.InOctets, Sent = row.OutOctets
        };
    }

    private static bool Included(in IfRow row)
    {
        if (row.Type == 24 || (row.Flags & 2) != 0 || row.InterfaceGuid == Guid.Empty) return false;
        // Include physical, VPN and virtual Ethernet adapters, omitting inactive plumbing.
        return (row.Flags & 1) != 0 || row.OperStatus == 1 || row.Type is 6 or 71;
    }

    private static bool Connected(in IfRow row) => row.OperStatus == 1 && row.MediaConnectState != 2;

    private static ulong NormalizeSpeed(ulong value) => value == ulong.MaxValue ? 0 : value;

    internal static string? ChooseDefault(IReadOnlyList<NetworkDevice> devices)
    {
        var costs = DefaultRouteCosts();
        return devices.OrderBy(x => x.Connected && x.Hardware && costs.ContainsKey(x.Index) ? 0
            : x.Connected && costs.ContainsKey(x.Index) ? 1 : x.Connected && x.Hardware ? 2
            : x.Connected ? 3 : x.Hardware ? 4 : 5)
            .ThenBy(x => costs.GetValueOrDefault(x.Index, ulong.MaxValue))
            .ThenBy(x => x.Index).FirstOrDefault()?.Id;
    }

    private static Dictionary<uint, ulong> DefaultRouteCosts()
    {
        var costs = new Dictionary<uint, ulong>();
        if (GetIpForwardTable2(0, out var table) != 0) return costs;
        try
        {
            var count = *(uint*)table;
            var rows = (ForwardRow*)(table + 8);
            for (var i = 0; i < count; i++)
            {
                var row = rows[i];
                if (row.DestinationPrefix.PrefixLength != 0 || row.ValidLifetime == 0 || row.Loopback != 0) continue;
                IpInterfaceRow iface = default;
                iface.Family = row.DestinationPrefix.Prefix.Family;
                iface.InterfaceLuid = row.InterfaceLuid;
                iface.InterfaceIndex = row.InterfaceIndex;
                var result = GetIpInterfaceEntry(ref iface);
                if (result == 0 && iface.DisableDefaultRoutes != 0) continue;
                var cost = (ulong)row.Metric + (result == 0 ? iface.Metric : 0);
                if (!costs.TryGetValue(row.InterfaceIndex, out var previous) || cost < previous)
                    costs[row.InterfaceIndex] = cost;
            }
        }
        finally { FreeMibTable(table); }
        return costs;
    }

    /// <summary>NLM connection connectivity is keyed by adapter GUID, never the machine-wide flag.</summary>
    internal static Dictionary<Guid, uint>? ReadConnectivity()
    {
        var initialized = CoInitializeEx(0, 0); // MTA. Existing STA initialization is also usable.
        nint manager = 0, enumerator = 0;
        try
        {
            var clsid = new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
            var iid = new Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B");
            if (CoCreateInstance(ref clsid, 0, 23, ref iid, out manager) < 0) return null;
            var getConnections = (delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)manager)[9];
            if (getConnections(manager, &enumerator) < 0) return null;
            var next = (delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)(*(nint**)enumerator)[8];
            var states = new Dictionary<Guid, uint>();
            for (var i = 0; i < 1024; i++)
            {
                nint connection = 0;
                uint fetched = 0;
                var result = next(enumerator, 1, &connection, &fetched);
                if (result < 0) return null;
                if (fetched == 0) break;
                try
                {
                    var getAdapter = (delegate* unmanaged[Stdcall]<nint, Guid*, int>)(*(nint**)connection)[12];
                    var getConnectivity = (delegate* unmanaged[Stdcall]<nint, uint*, int>)(*(nint**)connection)[10];
                    Guid adapterId = default;
                    uint connectivity = 0;
                    if (getAdapter(connection, &adapterId) >= 0 && getConnectivity(connection, &connectivity) >= 0)
                        states[adapterId] = states.GetValueOrDefault(adapterId) | connectivity;
                }
                finally { GpuDevices.Release(connection); }
            }
            return states;
        }
        finally
        {
            GpuDevices.Release(enumerator);
            GpuDevices.Release(manager);
            if (initialized >= 0) CoUninitialize();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IfRow
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        public fixed char Alias[257];
        public fixed char Description[257];
        public uint PhysicalAddressLength;
        public fixed byte PhysicalAddress[32];
        public fixed byte PermanentPhysicalAddress[32];
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte Flags;
        public uint OperStatus, AdminStatus, MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed, ReceiveLinkSpeed, InOctets, InUcastPkts, InNUcastPkts, InDiscards,
            InErrors, InUnknownProtos, InUcastOctets, InMulticastOctets, InBroadcastOctets, OutOctets,
            OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors, OutUcastOctets, OutMulticastOctets,
            OutBroadcastOctets, OutQLen;
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct SockaddrInet
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public uint Alignment;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AddressPrefix { public SockaddrInet Prefix; public byte PrefixLength; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ForwardRow
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public AddressPrefix DestinationPrefix;
        public SockaddrInet NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime, PreferredLifetime, Metric, Protocol;
        public byte Loopback, AutoconfigureAddress, Publish, Immortal;
        public uint Age, Origin;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IpInterfaceRow
    {
        public ushort Family;
        public ulong InterfaceLuid;
        public uint InterfaceIndex, MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval, MaxRouterAdvertisementInterval;
        public byte AdvertisingEnabled, ForwardingEnabled, WeakHostSend, WeakHostReceive,
            UseAutomaticMetric, UseNeighborUnreachabilityDetection, ManagedAddressConfigurationSupported,
            OtherStatefulConfigurationSupported, AdvertiseDefaultRoute;
        public uint RouterDiscoveryBehavior, DadTransmits, BaseReachableTime, RetransmitTime,
            PathMtuDiscoveryTimeout, LinkLocalAddressBehavior, LinkLocalAddressTimeout;
        public fixed uint ZoneIndices[16];
        public uint SitePrefixLength, Metric, NlMtu;
        public byte Connected, SupportsWakeUpPatterns, SupportsNeighborDiscovery, SupportsRouterDiscovery;
        public uint ReachableTime;
        public byte TransmitOffload, ReceiveOffload, DisableDefaultRoutes;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)] private static extern uint GetIfTable2(out nint table);
    [DllImport("iphlpapi.dll", ExactSpelling = true)] private static extern void FreeMibTable(nint table);
    [DllImport("iphlpapi.dll", ExactSpelling = true)] private static extern uint GetIfEntry2(IfRow* row);
    [DllImport("iphlpapi.dll", ExactSpelling = true)] private static extern uint GetIpForwardTable2(ushort family, out nint table);
    [DllImport("iphlpapi.dll", ExactSpelling = true)] private static extern uint GetIpInterfaceEntry(ref IpInterfaceRow row);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
}
