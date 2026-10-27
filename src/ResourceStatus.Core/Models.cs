namespace ResourceStatus.Core;

public enum WidgetMode { Compact, Expanded }
public enum ThemeMode { Dark, Light, System }
public enum MetricKind { Cpu, Ram, Gpu, Network, Disk }
public enum ReadingState { Ready, WarmingUp, Unsupported, AccessDenied, Error }
public enum UsageSeverity { Normal, Warning, Critical }

public readonly record struct Reading<T>(T? Value, ReadingState State, string? Detail = null) where T : struct
{
    public bool IsReady => State == ReadingState.Ready && Value.HasValue;
    public static Reading<T> Ready(T value) => new(value, ReadingState.Ready);
    public static Reading<T> Missing(ReadingState state, string? detail = null) => new(null, state, detail);
}

public sealed record DeviceOption(string Id, string Name);
public sealed record DiskDeviceOption(string Id, string Name, string Model, int? Number);
public sealed record DiskSnapshot(DiskDeviceOption Device, Reading<double> ReadBytesPerSecond, Reading<double> WriteBytesPerSecond);
public sealed record DiskHistorySample(double? ReadBytesPerSecond, double? WriteBytesPerSecond);
public sealed record CpuSnapshot(string Name, Reading<double> Usage);
public sealed record RamSnapshot(Reading<double> Usage, Reading<ulong> UsedBytes, Reading<ulong> TotalBytes);
public sealed record GpuSnapshot(string? DeviceId, string Name, Reading<double> Usage,
    Reading<ulong> DedicatedUsedBytes, Reading<ulong> DedicatedTotalBytes, Reading<ulong> SharedUsedBytes);
public sealed record NetworkSnapshot(string? DeviceId, string Name, Reading<double> DownloadBytesPerSecond,
    Reading<double> UploadBytesPerSecond, Reading<double> LinkUsage,
    Reading<ulong> ReceiveLinkBitsPerSecond, Reading<ulong> TransmitLinkBitsPerSecond, string ConnectionStatus);
public sealed record SystemSnapshot(DateTimeOffset Timestamp, CpuSnapshot Cpu, RamSnapshot Ram,
    GpuSnapshot Gpu, NetworkSnapshot Network)
{
    public IReadOnlyDictionary<string, DiskSnapshot> Disks { get; init; } = System.Collections.Immutable.ImmutableDictionary<string, DiskSnapshot>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
}

public sealed record HistorySample(DateTimeOffset Timestamp, double? Cpu, double? Ram,
    double? Gpu, double? Download, double? Upload, string? GpuId, string? NetworkId, double? LinkUsage = null)
{
    public IReadOnlyDictionary<string, DiskHistorySample> Disks { get; init; } = System.Collections.Immutable.ImmutableDictionary<string, DiskHistorySample>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
}

public static class ElementIds
{
    public const string Usage = "usage", Gauge = "gauge", Graph = "graph", Name = "name";
    public const string Used = "used", Total = "total", DedicatedUsed = "dedicatedUsed",
        DedicatedTotal = "dedicatedTotal", SharedUsed = "sharedUsed";
    public const string Download = "download", Upload = "upload", DownloadGraph = "downloadGraph",
        UploadGraph = "uploadGraph", ReceiveLink = "receiveLink", TransmitLink = "transmitLink", Connection = "connection";
    public const string Read = "read", Write = "write", ReadGraph = "readGraph", WriteGraph = "writeGraph";
}

public sealed class MetricDisplayOptions
{
    public bool Visible { get; set; } = true;
    public HashSet<string> Elements { get; set; } = [];
    public bool Has(string id) => Elements.Contains(id);
}

public sealed class DisplayProfile
{
    public Dictionary<MetricKind, MetricDisplayOptions> Items { get; set; } = [];
    public List<DiskSourceSelection> DiskSources { get; set; } = [new() { Id = DiskCatalog.TotalId, Name = DiskCatalog.TotalDevice.Name }];
}

public sealed class DiskSourceSelection
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class DisplayProfiles
{
    public DisplayProfile Compact { get; set; } = DisplayCatalog.CreateDefaults(WidgetMode.Compact);
    public DisplayProfile Expanded { get; set; } = DisplayCatalog.CreateDefaults(WidgetMode.Expanded);
    public DisplayProfile For(WidgetMode mode) => mode == WidgetMode.Compact ? Compact : Expanded;
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public string? MonitorId { get; set; }
    public double? MonitorOffsetX { get; set; }
    public double? MonitorOffsetY { get; set; }
}

public sealed class UsageColorOptions
{
    public bool Enabled { get; set; } = true;
    public int WarningThreshold { get; set; } = 80;
    public int CriticalThreshold { get; set; } = 95;
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public WidgetMode WidgetMode { get; set; } = WidgetMode.Compact;
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public bool AlwaysOnTop { get; set; } = true;
    public bool LockWidgetSize { get; set; }
    public bool ClickThrough { get; set; }
    public bool IncludePrereleaseUpdates { get; set; }
    public bool Transparency { get; set; } = true;
    public int BackgroundTransparency { get; set; } = 25;
    public bool StartWithWindows { get; set; }
    public string? SelectedGpuId { get; set; }
    public string? SelectedNetworkId { get; set; }
    public DisplayProfiles DisplayProfiles { get; set; } = new();
    public Dictionary<WidgetMode, WindowPlacement> Placements { get; set; } = [];
    public Dictionary<WidgetMode, double> LockedHeights { get; set; } = [];
    public Dictionary<MetricKind, UsageColorOptions> UsageColors { get; set; } = UsageColorPolicy.CreateDefaults();
}

public sealed record DisplayElement(string Id, string Label);

public interface IMetricsProvider : IDisposable
{
    IReadOnlyList<DeviceOption> Gpus { get; }
    IReadOnlyList<DeviceOption> Networks { get; }
    IReadOnlyList<DiskDeviceOption> Disks { get; }
    SystemSnapshot Read(string? gpuId, string? networkId);
    void Reset();
}
