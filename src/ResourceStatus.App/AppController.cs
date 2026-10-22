using System;
using System.Collections.Generic;
using System.IO;
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
    public SystemSnapshot Current { get; private set; } = EmptySnapshot();
    public IReadOnlyList<DeviceOption> Gpus { get; private set; } = [];
    public IReadOnlyList<DeviceOption> Networks { get; private set; } = [];
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
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (_stop.IsCancellationRequested) return;
                            Current = sample;
                            // Keep the previous list instances when unchanged so subscribers can compare by reference.
                            if (!SameDevices(Gpus, gpus)) Gpus = gpus;
                            if (!SameDevices(Networks, networks)) Networks = networks;
                            CollectionError = null;
                            History.Add(sample);
                            SnapshotUpdated?.Invoke(this, EventArgs.Empty);
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            CollectionError = ex.Message;
                            Current = EmptySnapshot(ReadingState.Error, "측정 중 오류가 발생했습니다. 다음 갱신에서 다시 시도합니다.");
                            History.Add(Current);
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
                        Current = EmptySnapshot(ReadingState.Error, ex.Message);
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
    public void PreviewPreferences() => PreferencesChanged?.Invoke(this, EventArgs.Empty);

    public void Save(bool preferences = true)
    {
        SettingsSaving?.Invoke(this, EventArgs.Empty);
        Volatile.Write(ref _gpuId, Settings.SelectedGpuId);
        Volatile.Write(ref _networkId, Settings.SelectedNetworkId);
        try { _store.Save(Settings); SaveError = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SaveError = "설정을 저장하지 못했습니다: " + ex.Message; }
        if (preferences) PreferencesChanged?.Invoke(this, EventArgs.Empty);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameDevices(IReadOnlyList<DeviceOption> a, IReadOnlyList<DeviceOption> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Id != b[i].Id || a[i].Name != b[i].Name) return false;
        return true;
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
            new(null, "GPU", d, b, b, b), new(null, "네트워크", d, d, d, b, b, "확인 불가"));
    }
}

internal sealed class DemoMetricsProvider : IMetricsProvider
{
    private int _tick;
    public IReadOnlyList<DeviceOption> Gpus { get; } = [new("demo-gpu", "데모 GPU")];
    public IReadOnlyList<DeviceOption> Networks { get; } = [new("demo-network", "데모 Wi-Fi")];
    public SystemSnapshot Read(string? gpuId, string? networkId)
    {
        double phase = _tick++ / 5d;
        var cpu = 25 + Math.Sin(phase) * 15;
        var ram = 62 + Math.Sin(phase / 4) * 2;
        var gpu = 38 + Math.Cos(phase) * 20;
        double down = 12_400_000 + Math.Sin(phase) * 4_000_000, up = 820_000 + Math.Cos(phase) * 200_000;
        return new(DateTimeOffset.UtcNow, new("데모 CPU · 8코어", Reading<double>.Ready(cpu)),
            new(Reading<double>.Ready(ram), Reading<ulong>.Ready((ulong)(32UL * 1024 * 1024 * 1024 * ram / 100)), Reading<ulong>.Ready(32UL * 1024 * 1024 * 1024)),
            new("demo-gpu", "데모 GPU", Reading<double>.Ready(gpu), Reading<ulong>.Ready(3_435_973_836), Reading<ulong>.Ready(8UL * 1024 * 1024 * 1024), Reading<ulong>.Ready(268_435_456)),
            new("demo-network", "데모 Wi-Fi", Reading<double>.Ready(down), Reading<double>.Ready(up), Reading<double>.Ready(down * 8 / 1_000_000_000 * 100),
                Reading<ulong>.Ready(1_000_000_000), Reading<ulong>.Ready(1_000_000_000), "인터넷 연결"));
    }
    public void Reset() { }
    public void Dispose() { }
}
