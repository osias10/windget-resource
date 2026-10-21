using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ResourceStatus.Core;

namespace ResourceStatus.Native;

/// <summary>Serial, in-process Windows collection. No elevated service or child process is required.</summary>
public sealed class WindowsMetricsProvider : IMetricsProvider
{
    private readonly object _gate = new();
    private PdhQuery _pdh;
    private List<GpuDevice> _gpuDevices = [];
    private IReadOnlyList<DeviceOption> _gpus = Array.Empty<DeviceOption>();
    private IReadOnlyList<DeviceOption> _networks = Array.Empty<DeviceOption>();
    private readonly string _cpuName;
    private Dictionary<Guid, uint>? _connectivity;
    private long _lastDevices, _lastConnectivity, _lastRead, _lastNetworkScan;
    private List<NetworkDevice> _networkDevices = [];
    private string? _defaultNetworkId, _lastNetworkSelection;
    private (NetworkDevice Device, long Timestamp)? _previousNetwork;
    private bool _disposed;
    private ReadingState _gpuEnumerationState = ReadingState.Unsupported;
    private string? _gpuEnumerationDetail;

    public IReadOnlyList<DeviceOption> Gpus => Volatile.Read(ref _gpus);
    public IReadOnlyList<DeviceOption> Networks => Volatile.Read(ref _networks);

    public WindowsMetricsProvider()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows에서만 실행할 수 있습니다.");
        _cpuName = ReadCpuName();
        _pdh = new PdhQuery();
        RefreshGpus();
        try
        {
            var networks = NetworkDevices.Read();
            UpdateNetworkOptions(networks);
            _defaultNetworkId = NetworkDevices.ChooseDefault(networks);
            _networkDevices = networks;
        }
        catch (Win32Exception) { }
        _lastDevices = Stopwatch.GetTimestamp();
    }

    public SystemSnapshot Read(string? gpuId, string? networkId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = Stopwatch.GetTimestamp();
            // Reset sampling after sleep, suspend or an unusually long scheduling delay.
            if (_lastRead != 0 && Stopwatch.GetElapsedTime(_lastRead, now) > TimeSpan.FromSeconds(5)) ResetCore();
            var refresh = _lastDevices == 0 || Stopwatch.GetElapsedTime(_lastDevices, now) >= TimeSpan.FromSeconds(30);
            if (refresh)
            {
                RefreshGpus();
                if (_pdh.CollectionFailed) { _pdh.Dispose(); _pdh = new PdhQuery(); }
                else if (_pdh.NeedsRetry) _pdh.RetryMissing();
                _lastDevices = now;
            }
            _pdh.Collect();
            _lastRead = now;
            var cpu = new CpuSnapshot(_cpuName, _pdh.Scalar("cpu"));
            var ram = ReadRam();
            var gpu = ReadGpu(gpuId);
            var network = ReadNetwork(networkId, now, refresh);
            return new(DateTimeOffset.Now, cpu, ram, gpu, network);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ResetCore();
        }
    }

    private void ResetCore()
    {
        _pdh.Dispose();
        _pdh = new PdhQuery();
        _previousNetwork = null;
        _lastRead = _lastDevices = _lastConnectivity = _lastNetworkScan = 0;
        _connectivity = null;
    }

    private void RefreshGpus()
    {
        try
        {
            _gpuDevices = GpuDevices.Enumerate();
            Volatile.Write(ref _gpus, _gpuDevices.Select(x => new DeviceOption(x.Id, x.Name)).ToArray());
            _gpuEnumerationState = _gpuDevices.Count > 0 ? ReadingState.Ready : ReadingState.Unsupported;
            _gpuEnumerationDetail = null;
        }
        // Any enumeration failure is a GPU-only error; it must never stop CPU, RAM and network collection.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _gpuDevices = [];
            Volatile.Write(ref _gpus, Array.Empty<DeviceOption>());
            _gpuEnumerationState = ex.HResult == unchecked((int)0x80070005) ? ReadingState.AccessDenied : ReadingState.Error;
            _gpuEnumerationDetail = ex.Message;
        }
    }

    private GpuSnapshot ReadGpu(string? selected)
    {
        // Missing explicit selections are reported; never silently relabel another device as it.
        var device = selected is null ? _gpuDevices.FirstOrDefault()
            : _gpuDevices.FirstOrDefault(x => x.Id.Equals(selected, StringComparison.OrdinalIgnoreCase)
                || x.Luid.Equals(selected, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            var state = _gpuEnumerationState == ReadingState.Ready ? ReadingState.Unsupported : _gpuEnumerationState;
            var bytes = Reading<ulong>.Missing(state, _gpuEnumerationDetail ?? "선택한 GPU를 찾을 수 없습니다.");
            return new(selected, "GPU를 찾을 수 없음", Reading<double>.Missing(state, bytes.Detail), bytes, bytes, bytes);
        }
        var dedicated = ReadGpuMemory("dedicated", device.Luid);
        var shared = ReadGpuMemory("shared", device.Luid);
        // An idle hybrid dGPU may have no engine instance at all. When DXGI lists it and its adapter memory
        // counters read, that is idle zero rather than an unsupported counter that keeps being restarted.
        var engines = _pdh.Array("gpu", device.Luid, dedicated.State == ReadingState.Ready && shared.State == ReadingState.Ready);
        var usage = engines.State != ReadingState.Ready ? null
            : engines.Values.Count == 0 ? 0d : MetricMath.AggregateGpuEngines(engines.Values, device.Luid);
        var reading = usage.HasValue ? Reading<double>.Ready(usage.Value)
            : Reading<double>.Missing(engines.State == ReadingState.Ready ? ReadingState.Unsupported : engines.State, engines.Detail);
        return new(device.Id, device.Name, reading, dedicated, Reading<ulong>.Ready(device.DedicatedBytes), shared);
    }

    private Reading<ulong> ReadGpuMemory(string counter, string id)
    {
        var data = _pdh.Array(counter, id);
        if (data.State != ReadingState.Ready) return Reading<ulong>.Missing(data.State, data.Detail);
        // Adapter-level counters already include all processes. Summing process memory double-counts shared allocations.
        var matches = data.Values.Where(x => x.Key.StartsWith("luid_" + id + "_", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return Reading<ulong>.Missing(ReadingState.Unsupported);
        var total = matches.Sum(x => x.Value);
        return double.IsFinite(total) && total >= 0 && total < ulong.MaxValue
            ? Reading<ulong>.Ready((ulong)total) : Reading<ulong>.Missing(ReadingState.Error);
    }

    private static RamSnapshot ReadRam()
    {
        MemoryStatus memory = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory) || memory.TotalPhysical == 0 || memory.AvailablePhysical > memory.TotalPhysical)
        {
            var error = Marshal.GetLastWin32Error();
            var state = error == 5 ? ReadingState.AccessDenied : ReadingState.Error;
            return new(Reading<double>.Missing(state), Reading<ulong>.Missing(state), Reading<ulong>.Missing(state));
        }
        var used = memory.TotalPhysical - memory.AvailablePhysical;
        return new(Reading<double>.Ready(used * 100.0 / memory.TotalPhysical),
            Reading<ulong>.Ready(used), Reading<ulong>.Ready(memory.TotalPhysical));
    }

    private NetworkSnapshot ReadNetwork(string? selected, long now, bool refresh)
    {
        // Each tick reads only the selected adapter's row. The full table and default selection are refreshed
        // every 5 s, on a selection change, and immediately when that adapter disappears or the default disconnects.
        var scan = refresh || _lastNetworkScan == 0 || selected != _lastNetworkSelection
            || Stopwatch.GetElapsedTime(_lastNetworkScan, now) >= TimeSpan.FromSeconds(5);
        _lastNetworkSelection = selected;
        NetworkDevice? device = null;
        var networkTimestamp = 0L;
        var changed = false;
        if (!scan && FindNetwork(selected ?? _defaultNetworkId) is { } cached)
        {
            device = NetworkDevices.ReadEntry(cached);
            networkTimestamp = Stopwatch.GetTimestamp();
            scan = device is null || (selected is null && cached.Connected && !device.Connected);
        }
        if (scan)
        {
            List<NetworkDevice> devices;
            try { devices = NetworkDevices.Read(); }
            catch (Win32Exception ex)
            {
                _previousNetwork = null;
                _lastNetworkScan = 0;
                return MissingNetwork(selected, "네트워크 확인 불가", ex.NativeErrorCode == 5 ? ReadingState.AccessDenied : ReadingState.Error, ex.Message);
            }
            networkTimestamp = Stopwatch.GetTimestamp();
            _networkDevices = devices;
            _lastNetworkScan = now;
            changed = UpdateNetworkOptions(devices);
            if (refresh || changed || _defaultNetworkId is null || !devices.Any(x => x.Id == _defaultNetworkId && x.Connected))
                _defaultNetworkId = NetworkDevices.ChooseDefault(devices);
            device = FindNetwork(selected ?? _defaultNetworkId);
        }
        if (device is null)
        {
            _previousNetwork = null;
            return MissingNetwork(selected, "어댑터를 찾을 수 없음", ReadingState.Unsupported);
        }
        if (_lastConnectivity == 0 || changed || Stopwatch.GetElapsedTime(_lastConnectivity, now) >= TimeSpan.FromSeconds(5))
        {
            _connectivity = NetworkDevices.ReadConnectivity();
            _lastConnectivity = now;
        }
        var connectionStatus = NetworkConnectivity.Label(device.Connected,
            _connectivity is not null && _connectivity.TryGetValue(device.Guid, out var flags) ? flags : (uint?)null);
        double? download = null, upload = null;
        if (_previousNetwork is { } previous && previous.Device.Id == device.Id
            && previous.Device.Luid == device.Luid && previous.Device.Connected == device.Connected)
        {
            var elapsed = Stopwatch.GetElapsedTime(previous.Timestamp, networkTimestamp);
            download = MetricMath.NetworkDelta(previous.Device.Received, device.Received, elapsed);
            upload = MetricMath.NetworkDelta(previous.Device.Sent, device.Sent, elapsed);
        }
        _previousNetwork = (device, networkTimestamp);
        var link = MetricMath.LinkUsage(download, upload, device.ReceiveLink, device.TransmitLink);
        return new(device.Id, device.Name, Rate(download), Rate(upload),
            link.HasValue ? Reading<double>.Ready(link.Value)
                : Reading<double>.Missing(download.HasValue && upload.HasValue ? ReadingState.Unsupported : ReadingState.WarmingUp),
            Link(device.ReceiveLink), Link(device.TransmitLink), connectionStatus);
    }

    private NetworkDevice? FindNetwork(string? id)
    {
        if (id is null) return null;
        foreach (var device in _networkDevices)
            if (device.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return device;
        return null;
    }

    private bool UpdateNetworkOptions(List<NetworkDevice> devices)
    {
        var options = devices.Select(x => new DeviceOption(x.Id, x.Name)).OrderBy(x => x.Name).ToArray();
        if (_networks.SequenceEqual(options)) return false;
        Volatile.Write(ref _networks, options);
        return true;
    }

    private static Reading<double> Rate(double? value) => value.HasValue
        ? Reading<double>.Ready(value.Value) : Reading<double>.Missing(ReadingState.WarmingUp);
    private static Reading<ulong> Link(ulong value) => value > 0
        ? Reading<ulong>.Ready(value) : Reading<ulong>.Missing(ReadingState.Unsupported);

    private static NetworkSnapshot MissingNetwork(string? id, string name, ReadingState state, string? detail = null)
        => new(id, name, Reading<double>.Missing(state, detail), Reading<double>.Missing(state, detail),
            Reading<double>.Missing(state, detail), Reading<ulong>.Missing(state, detail), Reading<ulong>.Missing(state, detail), "확인 불가");

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return "CPU"; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pdh.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
