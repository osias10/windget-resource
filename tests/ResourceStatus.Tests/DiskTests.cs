using ResourceStatus.Core;
using ResourceStatus.Native;

internal static class DiskTests
{
    private static readonly DiskDeviceOption First = new("pnp:first", "디스크 0 (C:)", "SSD", 0);
    private static readonly DiskDeviceOption Second = new("pnp:second", "디스크 1 (D:, E:)", "HDD", 1);

    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Disk defaults are visible, total-only, independent per mode and excluded from percentage alerts", () =>
        {
            var settings = new AppSettings();
            var small = settings.DisplayProfiles.Compact;
            var large = settings.DisplayProfiles.Expanded;
            Check(small.Items[MetricKind.Disk].Visible && large.Items[MetricKind.Disk].Visible, "New installs show disk");
            Check(small.Items[MetricKind.Disk].Elements.SetEquals([ElementIds.Read, ElementIds.Write]), "Compact defaults");
            Check(large.Items[MetricKind.Disk].Elements.SetEquals([ElementIds.Read, ElementIds.Write, ElementIds.ReadGraph, ElementIds.WriteGraph, ElementIds.Name]), "Expanded defaults");
            Check(small.DiskSources.Single().Id == DiskCatalog.TotalId, "Only total selected");
            DiskCatalog.TrySetSource(small, First, true);
            small.DiskSources[0].Name = "changed";
            Check(large.DiskSources.Single().Name == DiskCatalog.TotalDevice.Name, "Profile source objects independent");
            Check(!settings.UsageColors.ContainsKey(MetricKind.Disk) && !UsageColorPolicy.SupportedMetrics.Contains(MetricKind.Disk), "No disk percentages");
        });
        yield return ("Disk selection accepts total and multiple devices together but protects the final visible source", () =>
        {
            var profile = DisplayCatalog.CreateDefaults(WidgetMode.Compact);
            Check(!DiskCatalog.TrySetSource(profile, DiskCatalog.TotalDevice, false), "Cannot deselect only source");
            Check(DiskCatalog.TrySetSource(profile, First, true) && DiskCatalog.TrySetSource(profile, Second, true), "Multiple sources accepted");
            Check(profile.DiskSources.Count == 3, "Total and physical selections coexist");
            DiskCatalog.TrySetSource(profile, First with { Id = "PNP:FIRST", Name = "Renamed" }, true);
            Check(profile.DiskSources.Count == 3 && profile.DiskSources[1].Name == "Renamed", "IDs compare without case");
            DiskCatalog.TrySetSource(profile, DiskCatalog.TotalDevice, false);
            DiskCatalog.TrySetSource(profile, Second, false);
            Check(!DiskCatalog.TrySetSource(profile, First, false), "Protect last physical source too");
            profile.Items[MetricKind.Disk].Visible = false;
            Check(DiskCatalog.TrySetSource(profile, First, false), "Hidden item may be cleared programmatically");
        });
        yield return ("Disk normalization detaches aliases, retains absent devices and repairs invalid source lists", () =>
        {
            var shared = new DiskSourceSelection { Id = First.Id, Name = First.Name };
            var settings = new AppSettings();
            settings.DisplayProfiles.Compact.DiskSources = [shared, new() { Id = "PNP:FIRST" }, new() { Id = " " }, null!, new() { Id = "disk:total", Name = "invalid" }];
            settings.DisplayProfiles.Expanded.DiskSources = [shared];
            settings.UsageColors[MetricKind.Disk] = new();
            DisplayCatalog.Normalize(settings);
            Check(settings.DisplayProfiles.Compact.DiskSources.Count == 2, "Invalid and duplicate sources removed");
            Check(settings.DisplayProfiles.Compact.DiskSources[1].Name == "전체 합계", "Total canonical name");
            settings.DisplayProfiles.Compact.DiskSources[0].Name = "new";
            Check(settings.DisplayProfiles.Expanded.DiskSources[0].Name == First.Name, "Alias detached");
            Check(!settings.UsageColors.ContainsKey(MetricKind.Disk), "Ignore accidental percentage option");
            settings.DisplayProfiles.Compact.DiskSources = null!;
            DisplayCatalog.Normalize(settings);
            Check(settings.DisplayProfiles.Compact.DiskSources.Single().Id == DiskCatalog.TotalId, "Empty selection repairs to total");
        });
        yield return ("Legacy disk migration preserves all-hidden and partial settings without changing current explicit choices", () => WithSettings(path =>
        {
            foreach (var json in new[] { "{}", "{\"Theme\":\"Light\"}", "{\"DisplayProfiles\":null}", "{\"DisplayProfiles\":{\"Compact\":null}}", "{\"DisplayProfiles\":{\"Compact\":{\"Items\":null}}}", "{\"DisplayProfiles\":{\"Compact\":{\"Items\":{\"Cpu\":{\"Visible\":false,\"Elements\":[\"usage\"]},\"Ram\":{\"Visible\":false},\"Gpu\":{\"Visible\":false},\"Network\":{\"Visible\":false}}}}}" })
            {
                File.WriteAllText(path, json);
                var loaded = new SettingsStore(path).Load();
                Check(!loaded.DisplayProfiles.Compact.Items[MetricKind.Disk].Visible && !loaded.DisplayProfiles.Expanded.Items[MetricKind.Disk].Visible, "Legacy disk hidden");
                if (json.Contains("Cpu")) Check(loaded.DisplayProfiles.Compact.Items.Values.All(item => !item.Visible), "All-hidden survives migration");
            }
            var store = new SettingsStore(path);
            var current = new AppSettings();
            DiskCatalog.TrySetSource(current.DisplayProfiles.Expanded, First, true);
            DiskCatalog.TrySetSource(current.DisplayProfiles.Expanded, Second, true);
            current.DisplayProfiles.Compact.Items[MetricKind.Disk].Visible = false;
            current.DisplayProfiles.Expanded.Items[MetricKind.Disk].Elements = [ElementIds.ReadGraph];
            store.Save(current);
            var restored = store.Load();
            Check(!restored.DisplayProfiles.Compact.Items[MetricKind.Disk].Visible && restored.DisplayProfiles.Expanded.Items[MetricKind.Disk].Visible, "Per-mode visibility saved");
            Check(restored.DisplayProfiles.Expanded.DiskSources.Select(s => s.Id).SequenceEqual(new[] { DiskCatalog.TotalId, First.Id, Second.Id }), "Multi-source selection and order saved");
            Check(restored.DisplayProfiles.Expanded.Items[MetricKind.Disk].Elements.SetEquals([ElementIds.ReadGraph]), "Graph-only restored");
            restored.DisplayProfiles.Compact = DisplayCatalog.CreateDefaults(WidgetMode.Compact);
            Check(restored.DisplayProfiles.Expanded.DiskSources.Count == 3, "Reset only current mode");
        }));
        yield return ("Disk rates preserve zero, values above one hundred and GB/s without percent caps", () =>
        {
            foreach (var value in new[] { 0d, 150d, 1_234_567d, 5_000_000_000d })
                Check(DiskRateSamples.Reading(new("0 C:", 0, value)).Value == value, "Unscaled byte rate preserved");
            Check(Formatting.Rate(Reading<double>.Ready(5_000_000_000)) == "5 GB/s", "Decimal units");
            foreach (var invalid in new[] { -1d, double.NaN, double.PositiveInfinity })
                Check(DiskRateSamples.Reading(new("0", 0, invalid)).State == ReadingState.Error, "Invalid rates are not zero");
        });
        yield return ("Disk total uses only the exact total instance while each physical disk keeps its own rate", () =>
        {
            var batch = DiskRateSamples.Select([new("0 C:", 0, 40_000), new("1 D: E:", 0, 60_000), new("_Total", 0, 100_000)]);
            Check(batch.For(null, total: true).Value == 100_000 && batch.For(0).Value == 40_000 && batch.For(1).Value == 60_000, "No total double count");
            Check(DiskRateSamples.DeviceNumber("10 C: D:") == 10 && DiskRateSamples.DeviceNumber("0") == 0, "Parse multi-digit disk and no-volume names");
            Check(DiskRateSamples.DeviceNumber("_Total") is null && DiskRateSamples.DeviceNumber("x C:") is null, "Never assign total or invalid instance to physical disk");
        });
        yield return ("Disk counter failures and warmup remain local to the affected instance and direction", () =>
        {
            var reads = DiskRateSamples.Select([new("0 C:", 0, 1000), new("1 D:", 0xC0000BBA, 0), new("_Total", 0, 1000)]);
            var writes = DiskRateSamples.Select([new("0 C:", 5, 0), new("1 D:", 0, 2000)]);
            Check(reads.For(0).IsReady && reads.For(null, true).IsReady, "Warmup on disk1 leaves disk0 and total valid");
            Check(reads.For(1).State == ReadingState.WarmingUp && !writes.For(0).IsReady && writes.For(1).Value == 2000, "Direction states independent");
            Check(!reads.For(9).IsReady, "Absent disk never falls back");
            Check(DiskRateBatch.Missing(ReadingState.WarmingUp).For(0).State == ReadingState.WarmingUp, "Counter reset stays warming");
            var duplicate = DiskRateSamples.Select([new("0 C:", 0, 100), new("0 C:", 0, 200)]);
            Check(duplicate.For(0).State == ReadingState.Error, "Ambiguous duplicate never summed");
            var duplicateNumber = DiskRateSamples.Select([new("0 C:", 0, 100), new("0 D:", 0, 200)]);
            Check(duplicateNumber.For(0).State == ReadingState.Error, "Distinct instances with the same number are ambiguous");
        });
        yield return ("Disk topology tracks device identity and number rather than order, labels or drive letters", () =>
        {
            DiskDeviceOption[] original = [First, Second];
            Check(!DiskRateSamples.TopologyChanged(original, [Second, First with { Id = "PNP:FIRST", Name = "디스크 0 (F:)", Model = "Renamed" }]), "Labels/order/case do not reset sampling");
            Check(DiskRateSamples.TopologyChanged(original, [First with { Number = 2 }, Second]), "Disk number remapping resets disk counters");
            Check(DiskRateSamples.TopologyChanged(original, [First with { Id = "pnp:replacement" }, Second]), "Same-number replacement is a new device");
            Check(DiskRateSamples.TopologyChanged(original, [First]) && DiskRateSamples.TopologyChanged([First], original), "Removal and reconnect detected");
        });
        yield return ("Persistently invalid disk instances become errors and recover without poisoning adjacent disks", () =>
        {
            var tracker = new DiskRateStateTracker();
            var invalid = DiskRateSamples.Select([new("0 C:", 0xC0000BBA, 0), new("1 D:", 0, 9000)]);
            Check(tracker.Observe(invalid).For(0).State == ReadingState.WarmingUp, "New instance warms up");
            Check(tracker.Observe(invalid).For(0).State == ReadingState.WarmingUp, "Second invalid interval still prepares");
            var persistent = tracker.Observe(invalid);
            Check(persistent.For(0).State == ReadingState.Error && persistent.For(1).Value == 9000, "Persistent error remains isolated");
            var good = DiskRateSamples.Select([new("0 C:", 0, 42), new("1 D:", 0, 9000)]);
            Check(tracker.Observe(good).For(0).Value == 42, "Successful reading restores measurement");
            Check(tracker.Observe(invalid).For(0).State == ReadingState.WarmingUp, "Success cleared failure streak");
            tracker.Observe(DiskRateSamples.Select([new("1 D:", 0, 9000)]));
            Check(tracker.Observe(invalid).For(0).State == ReadingState.WarmingUp, "Reappearing instance starts a fresh baseline");
        });
        yield return ("Disk history snapshots detach mutable input and preserve per-device, per-direction gaps", () =>
        {
            var start = DateTimeOffset.UtcNow;
            var values = new Dictionary<string, DiskSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                [First.Id] = new(First, Reading<double>.Ready(500_000_000), Reading<double>.Ready(0)),
                [Second.Id] = new(Second, Reading<double>.Missing(ReadingState.Error), Reading<double>.Ready(700))
            };
            var history = new HistoryBuffer();
            history.Add(Snapshot(start) with { Disks = values });
            values[First.Id] = new(First, Reading<double>.Ready(900), Reading<double>.Ready(3));
            Check(history.Samples[0].Disks[First.Id].ReadBytesPerSecond == 500_000_000, "Historical sample detached");
            Check(history.Samples[0].Disks[Second.Id].ReadBytesPerSecond is null && history.Samples[0].Disks[Second.Id].WriteBytesPerSecond == 700, "Partial missing data preserved");
            history.Add(Snapshot(start.AddSeconds(4)) with { Disks = values });
            Check(history.Samples[1].Disks.Count == 0 && history.Samples[2].Disks[First.Id].ReadBytesPerSecond == 900, "Sleep creates gap without interpolation");
            var previousId = First.Id;
            var replacement = First with { Id = "pnp:replacement" };
            history.Add(Snapshot(start.AddSeconds(5)) with { Disks = new Dictionary<string, DiskSnapshot> { [replacement.Id] = new(replacement, Reading<double>.Ready(1), Reading<double>.Ready(1)) } });
            Check(!history.Samples[^1].Disks.ContainsKey(previousId), "Reused disk number does not inherit identity");
            var timeline = new UsageColorTimeline();
            timeline.Update(history.Samples, UsageColorPolicy.CreateDefaults());
            Check(timeline.GetLevel(MetricKind.Disk, start) == UsageSeverity.Normal, "High disk speeds never trigger percentage severity");
        });
    }

    private static SystemSnapshot Snapshot(DateTimeOffset time)
    {
        var d = Reading<double>.Ready(10);
        var b = Reading<ulong>.Ready(10);
        return new(time, new("CPU", d), new(d, b, b), new("gpu", "GPU", d, b, b, b), new("net", "NET", d, d, d, b, b, ""));
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void WithSettings(Action<string> run)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ResourceStatus.DiskTests"));
        var directory = Path.GetFullPath(Path.Combine(root, Guid.NewGuid().ToString("N")));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test path");
        Directory.CreateDirectory(directory);
        try { run(Path.Combine(directory, "settings.json")); }
        finally { Directory.Delete(directory, true); }
    }
}
