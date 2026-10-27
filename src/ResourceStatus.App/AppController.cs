using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ResourceStatus.Core;
using ResourceStatus.Native;

namespace ResourceStatus.App;

public sealed class AppController : IDisposable
{
    private readonly SettingsStore _store;
    private readonly CancellationTokenSource _stop = new();
    private IMetricsProvider? _provider;
    private Task? _loop;
    private int _visible = 1, _reset = 1;
    private string? _gpuId, _networkId;
    public AppSettings Settings { get; }
    public HistoryBuffer History { get; } = new();
    public UsageColorTimeline UsageColors { get; } = new();
    public SystemSnapshot Current { get; private set; } = EmptySnapshot();
    public IReadOnlyList<DeviceOption> Gpus { get; private set; } = [];
    public IReadOnlyList<DeviceOption> Networks { get; private set; } = [];
    public IReadOnlyList<DiskDeviceOption> Disks { get; private set; } = [];
    public bool IsDemo { get; }
    public string? SaveError { get; private set; }
    public string? CollectionError { get; private set; }
    /// <summary>A new collector sample (or collector error) is available.</summary>
    public event EventHandler? SnapshotUpdated;
    /// <summary>Settings were saved. Raised after <see cref="PreferencesChanged"/> for preference saves.</summary>
    public event EventHandler? SettingsChanged;
    /// <summary>Capture window state before the settings snapshot is written.</summary>
    public event EventHandler? SettingsSaving;
    /// <summary>Appearance or mode preferences changed (saved or previewed).</summary>
    public event EventHandler? PreferencesChanged;
    public event EventHandler? UpdateRequested;

    public void RequestUpdate() => UpdateRequested?.Invoke(this, EventArgs.Empty);

    public AppController(string settingsPath, bool demo)
    {
        IsDemo = demo;
        _store = new SettingsStore(settingsPath);
        Settings = _store.Load();
        _gpuId = Settings.SelectedGpuId;
        _networkId = Settings.SelectedNetworkId;
    }

    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(async () =>
        {
            try
            {
                _provider = IsDemo ? new DemoMetricsProvider() : new WindowsMetricsProvider();
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                do
                {
                    if (Volatile.Read(ref _visible) == 0) continue;
                    try
                    {
                        if (Interlocked.Exchange(ref _reset, 0) != 0) _provider.Reset();
                        var sample = _provider.Read(Volatile.Read(ref _gpuId), Volatile.Read(ref _networkId));
                        var gpus = new List<DeviceOption>(_provider.Gpus);
                        var networks = new List<DeviceOption>(_provider.Networks);
                        var disks = new List<DiskDeviceOption>(_provider.Disks);
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (_stop.IsCancellationRequested) return;
                            Current = sample;
                            // Keep the previous list instances when unchanged so subscribers can compare by reference.
                            if (!SameDevices(Gpus, gpus)) Gpus = gpus;
                            if (!SameDevices(Networks, networks)) Networks = networks;
                            if (!Disks.SequenceEqual(disks))
                            {
                                Disks = disks;
                                if (RefreshDiskNames()) Save(preferences: false);
                            }
                            CollectionError = null;
                            History.Add(sample);
                            UsageColors.Update(History.Samples, Settings.UsageColors);
                            SnapshotUpdated?.Invoke(this, EventArgs.Empty);
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            CollectionError = ex.Message;
                            Current = FailedSnapshot("측정 중 오류가 발생했습니다. 다음 갱신에서 다시 시도합니다.");
                            History.Add(Current);
                            UsageColors.Update(History.Samples, Settings.UsageColors);
                            SnapshotUpdated?.Invoke(this, EventArgs.Empty);
                        });
                    }
                } while (await timer.WaitForNextTickAsync(_stop.Token));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_stop.IsCancellationRequested)
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        CollectionError = ex.Message;
                        Current = FailedSnapshot(ex.Message);
                        SnapshotUpdated?.Invoke(this, EventArgs.Empty);
                    });
            }
            finally { _provider?.Dispose(); }
        });
    }

    public void SetVisible(bool visible)
    {
        if (Interlocked.Exchange(ref _visible, visible ? 1 : 0) == 0 && visible)
            Interlocked.Exchange(ref _reset, 1);
    }

    public void ResetSampling() => Interlocked.Exchange(ref _reset, 1);

    /// <summary>Preview appearance while a continuously changing control defers its disk write.</summary>
    public void PreviewPreferences()
    {
        UsageColors.Update(History.Samples, Settings.UsageColors);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Save(bool preferences = true)
    {
        SettingsSaving?.Invoke(this, EventArgs.Empty);
        Volatile.Write(ref _gpuId, Settings.SelectedGpuId);
        Volatile.Write(ref _networkId, Settings.SelectedNetworkId);
        try { _store.Save(Settings); SaveError = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SaveError = "설정을 저장하지 못했습니다: " + ex.Message; }
        if (preferences) PreviewPreferences();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameDevices(IReadOnlyList<DeviceOption> a, IReadOnlyList<DeviceOption> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Id != b[i].Id || a[i].Name != b[i].Name) return false;
        return true;
    }

    private bool RefreshDiskNames()
    {
        bool changed = false;
        foreach (var profile in new[] { Settings.DisplayProfiles.Compact, Settings.DisplayProfiles.Expanded })
            foreach (var selected in profile.DiskSources)
            {
                var device = Disks.FirstOrDefault(disk => disk.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase));
                if (device is null || device.Name == selected.Name) continue;
                selected.Name = device.Name;
                changed = true;
            }
        return changed;
    }

    private SystemSnapshot FailedSnapshot(string detail)
    {
        var missing = Reading<double>.Missing(ReadingState.Error, detail);
        return EmptySnapshot(ReadingState.Error, detail) with
        {
            Disks = new ReadOnlyDictionary<string, DiskSnapshot>(Disks.Append(DiskCatalog.TotalDevice)
                .DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(device => device.Id, device => new DiskSnapshot(device, missing, missing), StringComparer.OrdinalIgnoreCase))
        };
    }

    public void Dispose()
    {
        _stop.Cancel();
        // Do not block the UI dispatcher while the collector is dispatching its final sample.
    }

    public static SystemSnapshot EmptySnapshot(ReadingState state = ReadingState.WarmingUp, string? detail = null)
    {
        var d = Reading<double>.Missing(state, detail);
        var b = Reading<ulong>.Missing(state, detail);
        return new(DateTimeOffset.UtcNow, new("CPU", d), new(d, b, b),
            new(null, "GPU", d, b, b, b), new(null, "네트워크", d, d, d, b, b, "확인 불가"))
        {
            Disks = new ReadOnlyDictionary<string, DiskSnapshot>(new Dictionary<string, DiskSnapshot>(StringComparer.OrdinalIgnoreCase)
            { [DiskCatalog.TotalId] = new(DiskCatalog.TotalDevice, d, d) })
        };
    }
}

internal sealed class DemoMetricsProvider : IMetricsProvider
{
    private int _tick;
    public IReadOnlyList<DeviceOption> Gpus { get; } = [new("demo-gpu", "데모 GPU")];
    public IReadOnlyList<DeviceOption> Networks { get; } = [new("demo-network", "데모 Wi-Fi")];
    public IReadOnlyList<DiskDeviceOption> Disks { get; } = [DiskCatalog.TotalDevice,
        new("demo-disk-0", "디스크 0 (C:)", "데모 NVMe SSD", 0), new("demo-disk-1", "디스크 1 (D:, E:)", "데모 SATA SSD", 1)];
    public SystemSnapshot Read(string? gpuId, string? networkId)
    {
        double phase = _tick++ / 5d;
        var cpu = 25 + Math.Sin(phase) * 15;
        var ram = 62 + Math.Sin(phase / 4) * 2;
        var gpu = 38 + Math.Cos(phase) * 20;
        double down = 12_400_000 + Math.Sin(phase) * 4_000_000, up = 820_000 + Math.Cos(phase) * 200_000;
        double read0 = 120_000_000 + Math.Sin(phase) * 70_000_000, write0 = 32_000_000 + Math.Cos(phase) * 20_000_000;
        double read1 = 4_000_000 + Math.Cos(phase / 2) * 2_000_000, write1 = 12_000_000 + Math.Sin(phase / 2) * 8_000_000;
        var disks = new Dictionary<string, DiskSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [DiskCatalog.TotalId] = new(Disks[0], Reading<double>.Ready(read0 + read1), Reading<double>.Ready(write0 + write1)),
            [Disks[1].Id] = new(Disks[1], Reading<double>.Ready(read0), Reading<double>.Ready(write0)),
            [Disks[2].Id] = new(Disks[2], Reading<double>.Ready(read1), Reading<double>.Ready(write1))
        };
        return new(DateTimeOffset.UtcNow, new("데모 CPU · 8코어", Reading<double>.Ready(cpu)),
            new(Reading<double>.Ready(ram), Reading<ulong>.Ready((ulong)(32UL * 1024 * 1024 * 1024 * ram / 100)), Reading<ulong>.Ready(32UL * 1024 * 1024 * 1024)),
            new("demo-gpu", "데모 GPU", Reading<double>.Ready(gpu), Reading<ulong>.Ready(3_435_973_836), Reading<ulong>.Ready(8UL * 1024 * 1024 * 1024), Reading<ulong>.Ready(268_435_456)),
            new("demo-network", "데모 Wi-Fi", Reading<double>.Ready(down), Reading<double>.Ready(up), Reading<double>.Ready(down * 8 / 1_000_000_000 * 100),
                Reading<ulong>.Ready(1_000_000_000), Reading<ulong>.Ready(1_000_000_000), "인터넷 연결"))
        { Disks = new ReadOnlyDictionary<string, DiskSnapshot>(disks) };
    }
    public void Reset() { }
    public void Dispose() { }
}
