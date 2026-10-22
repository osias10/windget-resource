using System.Collections.ObjectModel;

namespace ResourceStatus.Core;

/// <summary>A time-based 60-second buffer; null values always remain gaps.</summary>
public sealed class HistoryBuffer
{
    private readonly List<HistorySample> _samples = [];
    private readonly ReadOnlyCollection<HistorySample> _readOnly;
    public HistoryBuffer() => _readOnly = _samples.AsReadOnly();
    public IReadOnlyList<HistorySample> Samples => _readOnly;

    public void Add(SystemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_samples.Count > 0)
        {
            var previous = _samples[^1];
            if (snapshot.Timestamp < previous.Timestamp) return;
            if (snapshot.Timestamp == previous.Timestamp) _samples.RemoveAt(_samples.Count - 1);
            else if (snapshot.Timestamp - previous.Timestamp > TimeSpan.FromSeconds(2.5))
            {
                _samples.Add(new HistorySample(previous.Timestamp.AddSeconds(1), null, null, null, null, null, null, null));
            }
        }
        _samples.Add(new HistorySample(snapshot.Timestamp,
            Value(snapshot.Cpu.Usage), Value(snapshot.Ram.Usage), Value(snapshot.Gpu.Usage),
            Value(snapshot.Network.DownloadBytesPerSecond), Value(snapshot.Network.UploadBytesPerSecond),
            snapshot.Gpu.DeviceId, snapshot.Network.DeviceId, Value(snapshot.Network.LinkUsage)));
        var cutoff = snapshot.Timestamp.AddSeconds(-60);
        _samples.RemoveAll(x => x.Timestamp < cutoff);
    }

    private static double? Value(Reading<double> reading) =>
        reading.IsReady && double.IsFinite(reading.Value!.Value) ? reading.Value.Value : null;
}
