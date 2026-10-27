namespace ResourceStatus.Core;

/// <summary>Keeps hysteresis state with each sample; evicting an old peak cannot recolor the remaining graph.</summary>
public sealed class UsageColorTimeline
{
    private sealed record Entry(HistorySample Sample, UsageSeverity[] Levels);
    private static readonly MetricKind[] Kinds = UsageColorPolicy.SupportedMetrics.ToArray();
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<DateTimeOffset, UsageSeverity[]> _levels = [];
    private Entry? _predecessor;
    private UsageColorPolicy.Options[]? _configuration;

    public void Update(IReadOnlyList<HistorySample> history, IReadOnlyDictionary<MetricKind, UsageColorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(options);
        var configured = Kinds.Select(kind => UsageColorPolicy.Configuration(options.TryGetValue(kind, out var value) ? value : null)).ToArray();
        var changed = Kinds.Select(kind => _configuration is null || _configuration[(int)kind] != configured[(int)kind]).ToArray();
        _configuration = configured;
        if (history.Count == 0)
        {
            _entries.Clear();
            _levels.Clear();
            _predecessor = null;
            return;
        }

        var samples = ChronologicalSamples(history);
        DateTimeOffset first = samples[0].Timestamp;
        // Retain only the immediate predecessor as a bounded hysteresis checkpoint.
        int evicted = 0;
        while (evicted < _entries.Count && _entries[evicted].Sample.Timestamp < first)
        {
            _predecessor = _entries[evicted];
            _levels.Remove(_predecessor.Sample.Timestamp);
            evicted++;
        }
        if (evicted > 0) _entries.RemoveRange(0, evicted);
        if (_predecessor?.Sample.Timestamp >= first) _predecessor = null;

        if (changed.Any(value => value))
        {
            // Re-evaluate changed metrics from Normal, while preserving the predecessor
            // state of unrelated metrics whose settings did not change.
            if (_predecessor is not null)
            {
                var previous = (UsageSeverity[])_predecessor.Levels.Clone();
                foreach (var kind in Kinds) if (changed[(int)kind]) previous[(int)kind] = UsageSeverity.Normal;
                _predecessor = _predecessor with { Levels = previous };
            }
            _entries.Clear();
            _levels.Clear();
        }

        int common = 0;
        while (common < _entries.Count && common < samples.Count && _entries[common].Sample == samples[common]) common++;
        // A replaced timestamp invalidates that sample and all following hysteresis states.
        for (int index = common; index < _entries.Count; index++) _levels.Remove(_entries[index].Sample.Timestamp);
        if (common < _entries.Count) _entries.RemoveRange(common, _entries.Count - common);
        for (int index = common; index < samples.Count; index++)
        {
            var sample = samples[index];
            var previous = _entries.Count > 0 ? _entries[^1] : _predecessor;
            bool contiguous = previous is not null && sample.Timestamp > previous.Sample.Timestamp &&
                sample.Timestamp - previous.Sample.Timestamp <= TimeSpan.FromSeconds(2.5);
            var levels = new UsageSeverity[Kinds.Length];
            foreach (var kind in Kinds)
            {
                bool sameDevice = previous is not null && kind switch
                {
                    MetricKind.Gpu => sample.GpuId == previous.Sample.GpuId,
                    MetricKind.Network => sample.NetworkId == previous.Sample.NetworkId,
                    _ => true
                };
                var priorLevel = contiguous && sameDevice ? previous!.Levels[(int)kind] : UsageSeverity.Normal;
                double? value = kind switch
                {
                    MetricKind.Cpu => sample.Cpu,
                    MetricKind.Ram => sample.Ram,
                    MetricKind.Gpu => sample.Gpu,
                    MetricKind.Network => sample.LinkUsage,
                    _ => null
                };
                levels[(int)kind] = UsageColorPolicy.Evaluate(value, configured[(int)kind], priorLevel);
            }
            _entries.Add(new Entry(sample, levels));
            _levels[sample.Timestamp] = levels;
        }
    }

    public UsageSeverity GetLevel(MetricKind kind, DateTimeOffset timestamp) =>
        Kinds.Contains(kind) && _levels.TryGetValue(timestamp, out var levels) ? levels[(int)kind] : UsageSeverity.Normal;

    private static IReadOnlyList<HistorySample> ChronologicalSamples(IReadOnlyList<HistorySample> history)
    {
        // HistoryBuffer already supplies sorted unique samples; only callers replacing
        // or reordering samples need this normalization. Last occurrence wins a duplicate.
        for (int index = 1; index < history.Count; index++)
        {
            if (history[index - 1].Timestamp < history[index].Timestamp) continue;
            var ordered = new SortedDictionary<DateTimeOffset, HistorySample>();
            foreach (var sample in history) ordered[sample.Timestamp] = sample;
            return ordered.Values.ToArray();
        }
        return history;
    }
}
