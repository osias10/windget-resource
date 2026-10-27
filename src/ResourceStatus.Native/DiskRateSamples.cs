using System.Collections.ObjectModel;
using System.Globalization;
using ResourceStatus.Core;

namespace ResourceStatus.Native;

internal sealed record DiskRateBatch(IReadOnlyDictionary<string, Reading<double>> Values,
    ReadingState State, string? Detail = null)
{
    internal static DiskRateBatch Missing(ReadingState state, string? detail = null)
        => new(new ReadOnlyDictionary<string, Reading<double>>(
            new Dictionary<string, Reading<double>>(StringComparer.OrdinalIgnoreCase)), state, detail);

    internal Reading<double> For(int? number, bool total = false)
    {
        Reading<double>? match = null;
        foreach (var pair in Values)
            if (total ? pair.Key.Equals("_Total", StringComparison.OrdinalIgnoreCase)
                : number.HasValue && DiskRateSamples.DeviceNumber(pair.Key) == number)
            {
                if (match.HasValue) return Reading<double>.Missing(ReadingState.Error, "디스크 번호에 해당하는 카운터 인스턴스가 중복되었습니다.");
                match = pair.Value;
            }
        return match ?? Reading<double>.Missing(State == ReadingState.Ready ? ReadingState.Unsupported : State,
            Detail ?? (State == ReadingState.Ready ? "디스크 성능 카운터 인스턴스가 없습니다." : null));
    }
}

/// <summary>Disk bytes/sec is already a PDH rate. Keep each instance's status and never clamp it to a percentage.</summary>
internal static class DiskRateSamples
{
    internal static bool TopologyChanged(IEnumerable<DiskDeviceOption> previous, IEnumerable<DiskDeviceOption> current)
        => !previous.Select(x => $"{x.Id}|{x.Number}").ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(current.Select(x => $"{x.Id}|{x.Number}"));

    internal static int? DeviceNumber(string instance)
    {
        var separator = instance.IndexOf(' ');
        var token = separator < 0 ? instance.AsSpan() : instance.AsSpan(0, separator);
        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : null;
    }

    internal static Reading<double> Reading(PdhSample sample)
        => sample.Status > 1 ? Reading<double>.Missing(PdhSamples.State(sample.Status), $"PDH 0x{sample.Status:X8}")
            : !double.IsFinite(sample.Value) || sample.Value < 0
                ? Reading<double>.Missing(ReadingState.Error, "디스크 전송 속도가 유효하지 않습니다.")
                : Reading<double>.Ready(sample.Value);

    internal static DiskRateBatch Select(IEnumerable<PdhSample> samples)
    {
        var values = new Dictionary<string, Reading<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples)
        {
            if (string.IsNullOrWhiteSpace(sample.Name)) continue;
            var reading = Reading(sample);
            // Duplicate instance names cannot be assigned safely to a physical disk. Never sum them.
            if (!values.TryAdd(sample.Name, reading))
                values[sample.Name] = Reading<double>.Missing(ReadingState.Error, "디스크 카운터 인스턴스가 중복되었습니다.");
        }
        return new(new ReadOnlyDictionary<string, Reading<double>>(values),
            values.Count > 0 ? ReadingState.Ready : ReadingState.Unsupported);
    }
}

/// <summary>A persistently invalid instance is an error, without hiding valid samples from its neighbors.</summary>
internal sealed class DiskRateStateTracker
{
    private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);

    internal DiskRateBatch Observe(DiskRateBatch batch)
    {
        var readings = new Dictionary<string, Reading<double>>(batch.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var name in _failures.Keys.Where(x => !readings.ContainsKey(x)).ToArray()) _failures.Remove(name);
        foreach (var (name, reading) in readings.ToArray())
        {
            if (reading.IsReady) { _failures.Remove(name); continue; }
            _failures.TryGetValue(name, out var failures);
            _failures[name] = failures = Math.Min(3, failures + 1);
            if (reading.State == ReadingState.WarmingUp && failures >= 3)
                readings[name] = Reading<double>.Missing(ReadingState.Error, reading.Detail);
        }
        return batch with { Values = new ReadOnlyDictionary<string, Reading<double>>(readings) };
    }
}
