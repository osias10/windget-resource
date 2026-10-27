namespace ResourceStatus.Core;

/// <summary>Display selection only: all physical disk counters are collected independently.</summary>
public static class DiskCatalog
{
    public const string TotalId = "disk:total";
    public static DiskDeviceOption TotalDevice { get; } = new(TotalId, "전체 합계", "", null);

    public static bool TrySetSource(DisplayProfile profile, DiskDeviceOption device, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Id);
        var selected = profile.DiskSources.FirstOrDefault(source => string.Equals(source.Id, device.Id, StringComparison.OrdinalIgnoreCase));
        if (enabled)
        {
            if (selected is null) profile.DiskSources.Add(new() { Id = device.Id, Name = device.Name });
            else selected.Name = device.Name;
        }
        else if (selected is not null)
        {
            if (profile.Items.TryGetValue(MetricKind.Disk, out var options) && options.Visible && profile.DiskSources.Count <= 1) return false;
            profile.DiskSources.Remove(selected);
        }
        return true;
    }

    internal static List<DiskSourceSelection> NormalizeSources(IEnumerable<DiskSourceSelection>? sources)
    {
        var result = new List<DiskSourceSelection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources ?? [])
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Id)) continue;
            var id = source.Id.Trim();
            if (!seen.Add(id)) continue;
            bool total = id.Equals(TotalId, StringComparison.OrdinalIgnoreCase);
            result.Add(new() { Id = total ? TotalId : id, Name = total ? TotalDevice.Name : string.IsNullOrWhiteSpace(source.Name) ? "디스크" : source.Name });
        }
        if (result.Count == 0) result.Add(new() { Id = TotalId, Name = TotalDevice.Name });
        return result;
    }
}
