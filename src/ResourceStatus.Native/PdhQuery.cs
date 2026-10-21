using System.Runtime.InteropServices;
using ResourceStatus.Core;

namespace ResourceStatus.Native;

/// <summary>One reusable query, with English names on every Windows display language.</summary>
internal sealed unsafe class PdhQuery : IDisposable
{
    private nint _query;
    private readonly Dictionary<string, Counter> _counters = new();
    private readonly List<PdhSample> _samples = [];
    private uint _collectStatus;
    private const uint MoreData = 0x800007D2, DoubleFormat = 0x200 | 0x8000, MaxBuffer = 64 * 1024 * 1024;
    private sealed class Counter(nint handle, uint status)
    {
        internal nint Handle { get; } = handle;
        internal uint Status { get; } = status;
        internal int Samples, ReadFailures;
        internal bool RestartRequested;
        // Reused across ticks so each array read is normally a single PDH call.
        internal nint Buffer;
        internal uint BufferBytes;
        internal void FreeBuffer()
        {
            if (Buffer != 0) Marshal.FreeHGlobal(Buffer);
            Buffer = 0;
            BufferBytes = 0;
        }
    }
    internal bool NeedsRetry => _query == 0 || _counters.Values.Any(x => x.Handle == 0 || x.RestartRequested) || _collectStatus != 0;
    internal bool CollectionFailed => _query == 0 || _collectStatus != 0;

    internal PdhQuery()
    {
        _collectStatus = PdhOpenQueryW(null, 0, out _query);
        RetryMissing();
    }

    internal void RetryMissing()
    {
        Add("cpu", @"\Processor Information(_Total)\% Processor Time");
        // An English wildcard is localized by PDH; GetFormattedCounterArray expands its instances
        // dynamically, unlike reading a single formatted counter value.
        Add("gpu", @"\GPU Engine(*)\Utilization Percentage");
        Add("dedicated", @"\GPU Adapter Memory(*)\Dedicated Usage");
        Add("shared", @"\GPU Adapter Memory(*)\Shared Usage");
    }

    private void Add(string key, string path)
    {
        if (_counters.TryGetValue(key, out var existing) && existing.Handle != 0)
        {
            if (!existing.RestartRequested) return;
            PdhRemoveCounter(existing.Handle);
            existing.FreeBuffer();
        }
        nint handle = 0;
        var status = _query == 0 ? _collectStatus : PdhAddEnglishCounterW(_query, path, 0, out handle);
        _counters[key] = new(handle, status);
    }

    internal void Collect()
    {
        _collectStatus = _query == 0 ? 0xC0000BBC : PdhCollectQueryData(_query);
        foreach (var counter in _counters.Values)
            counter.Samples = _collectStatus == 0 && counter.Handle != 0 ? Math.Min(2, counter.Samples + 1) : 0;
    }

    internal Reading<double> Scalar(string key)
    {
        var c = _counters[key];
        if (c.Handle == 0) return Missing<double>(c.Status);
        if (_collectStatus != 0) return Missing<double>(_collectStatus);
        if (c.Samples < 2) return Reading<double>.Missing(ReadingState.WarmingUp);
        var status = PdhGetFormattedCounterValue(c.Handle, DoubleFormat, out _, out var value);
        if (status != 0) return Reading<double>.Missing(Observe(c, State(status)), Error(status));
        if (value.Status > 1 || !double.IsFinite(value.Value))
            return Reading<double>.Missing(Observe(c, value.Status > 1 ? State(value.Status) : ReadingState.Error), Error(value.Status));
        Observe(c, ReadingState.Ready);
        return Reading<double>.Ready(Math.Clamp(value.Value, 0, 100));
    }

    /// <param name="idleWhenUnmatched">The adapter is known to exist, so a successful read with no instance for it is idle.</param>
    internal (List<KeyValuePair<string, double>> Values, ReadingState State, string? Detail) Array(string key, string? gpuLuid = null,
        bool idleWhenUnmatched = false)
    {
        var c = _counters[key];
        if (c.Handle == 0) return ([], State(c.Status), Error(c.Status));
        if (_collectStatus != 0) return ([], State(_collectStatus), Error(_collectStatus));
        if (key == "gpu" && c.Samples < 2) return ([], ReadingState.WarmingUp, null);
        // Read into the buffer kept from the last tick. Drivers may create instances at any time, so a short
        // buffer is re-sized with a fresh size query (a short call's returned size is unreliable).
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint bytes = c.BufferBytes, count = 0;
            var status = bytes == 0 ? MoreData : PdhGetFormattedCounterArrayW(c.Handle, DoubleFormat, ref bytes, out count, c.Buffer);
            if (status == MoreData)
            {
                bytes = 0;
                status = PdhGetFormattedCounterArrayW(c.Handle, DoubleFormat, ref bytes, out _, 0);
                if (status != MoreData && status != 0) return ([], Observe(c, State(status)), Error(status));
                if (bytes == 0) return ([], Observe(c, ReadingState.Unsupported), "성능 카운터 인스턴스가 없습니다.");
                if (bytes > MaxBuffer) return ([], ReadingState.Error, "성능 카운터 버퍼가 너무 큽니다.");
                c.FreeBuffer();
                var capacity = bytes + bytes / 4 + 4096; // Headroom for instances created by new processes.
                c.Buffer = Marshal.AllocHGlobal(checked((int)capacity));
                c.BufferBytes = capacity;
                continue;
            }
            if (status != 0) return ([], Observe(c, State(status)), Error(status));
            if (count == 0) return ([], Observe(c, ReadingState.Unsupported), "성능 카운터 인스턴스가 없습니다.");
            if ((ulong)count * (uint)sizeof(CounterItem) > bytes) return ([], Observe(c, ReadingState.Error), "성능 카운터 버퍼가 유효하지 않습니다.");
            // Compare names in place; only the selected adapter's instances become managed strings.
            var needle = gpuLuid is null ? null : "luid_" + gpuLuid + "_";
            var items = (CounterItem*)c.Buffer;
            _samples.Clear();
            for (var i = 0; i < count; i++)
            {
                var name = items[i].Name == 0 ? default : MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)items[i].Name);
                if (needle is not null && !name.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                _samples.Add(new(name.ToString(), items[i].Value.Status, items[i].Value.Value));
            }
            var selected = PdhSamples.Select(_samples, null, idleWhenUnmatched);
            _samples.Clear();
            return (selected.Values, Observe(c, selected.State), selected.Detail);
        }
        return ([], Observe(c, ReadingState.Error), "성능 카운터가 변경되었습니다.");
    }

    private static ReadingState Observe(Counter counter, ReadingState state)
    {
        if (state == ReadingState.Ready)
        {
            counter.ReadFailures = 0;
            counter.RestartRequested = false;
            return state;
        }
        counter.ReadFailures = Math.Min(3, counter.ReadFailures + 1);
        if (counter.ReadFailures >= 3) counter.RestartRequested = true;
        // A permanently invalid counter is an error, not a perpetual first-sample indicator.
        return state == ReadingState.WarmingUp && counter.ReadFailures >= 3 ? ReadingState.Error : state;
    }

    private static string Error(uint status) => $"PDH 0x{status:X8}";
    private static Reading<T> Missing<T>(uint status) where T : struct => Reading<T>.Missing(State(status), Error(status));
    private static ReadingState State(uint status) => PdhSamples.State(status);

    public void Dispose()
    {
        if (_query != 0) { PdhCloseQuery(_query); _query = 0; }
        foreach (var counter in _counters.Values) counter.FreeBuffer();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { public uint Status; public double Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public nint Name; public CounterValue Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? source, nuint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);
    [DllImport("pdh.dll", ExactSpelling = true)] private static extern uint PdhCloseQuery(nint query);
    [DllImport("pdh.dll", ExactSpelling = true)] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", ExactSpelling = true)] private static extern uint PdhRemoveCounter(nint counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bytes, out uint count, nint buffer);
}
