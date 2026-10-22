namespace ResourceStatus.Core;

/// <summary>The shared list of selectable elements and independent defaults for each layout.</summary>
public static class DisplayCatalog
{
    private static readonly IReadOnlyDictionary<MetricKind, IReadOnlyList<DisplayElement>> Catalog =
        new Dictionary<MetricKind, IReadOnlyList<DisplayElement>>
        {
            [MetricKind.Cpu] = Array.AsReadOnly(new[] {
                new DisplayElement(ElementIds.Usage, "사용률 숫자"), new(ElementIds.Gauge, "사용률 게이지"),
                new(ElementIds.Graph, "추이 그래프"), new(ElementIds.Name, "CPU 이름") }),
            [MetricKind.Ram] = Array.AsReadOnly(new[] {
                new DisplayElement(ElementIds.Usage, "사용률 숫자"), new(ElementIds.Used, "사용 용량"),
                new(ElementIds.Total, "전체 용량"), new(ElementIds.Gauge, "사용률 게이지"), new(ElementIds.Graph, "추이 그래프") }),
            [MetricKind.Gpu] = Array.AsReadOnly(new[] {
                new DisplayElement(ElementIds.Usage, "사용률 숫자"), new(ElementIds.Gauge, "사용률 게이지"),
                new(ElementIds.Graph, "추이 그래프"), new(ElementIds.Name, "GPU 이름"),
                new(ElementIds.DedicatedUsed, "전용 메모리 사용량"), new(ElementIds.DedicatedTotal, "전용 메모리 전체 용량"),
                new(ElementIds.SharedUsed, "공유 메모리 사용량") }),
            [MetricKind.Network] = Array.AsReadOnly(new[] {
                new DisplayElement(ElementIds.Download, "다운로드 속도"), new(ElementIds.Upload, "업로드 속도"),
                new(ElementIds.DownloadGraph, "다운로드 그래프"), new(ElementIds.UploadGraph, "업로드 그래프"),
                new(ElementIds.Usage, "링크 사용률 숫자"), new(ElementIds.Gauge, "링크 사용률 게이지"),
                new(ElementIds.ReceiveLink, "수신 링크 속도"), new(ElementIds.TransmitLink, "송신 링크 속도"),
                new(ElementIds.Name, "어댑터 이름"), new(ElementIds.Connection, "연결 상태") })
        };

    public static IReadOnlyList<DisplayElement> Elements(MetricKind kind) => Catalog[kind];

    public static string Label(MetricKind kind) => kind switch
    {
        MetricKind.Cpu => "CPU", MetricKind.Ram => "RAM", MetricKind.Gpu => "GPU", MetricKind.Network => "네트워크",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static DisplayProfile CreateDefaults(WidgetMode mode)
    {
        var profile = new DisplayProfile();
        foreach (var kind in Enum.GetValues<MetricKind>())
        {
            string[] elements = (mode, kind) switch
            {
                (WidgetMode.Compact, MetricKind.Network) => [ElementIds.Download, ElementIds.Upload, ElementIds.Connection],
                (WidgetMode.Compact, _) => [ElementIds.Usage, ElementIds.Gauge],
                (_, MetricKind.Cpu) => [ElementIds.Usage, ElementIds.Graph, ElementIds.Name],
                (_, MetricKind.Ram) => [ElementIds.Usage, ElementIds.Used, ElementIds.Total, ElementIds.Graph],
                (_, MetricKind.Gpu) => [ElementIds.Usage, ElementIds.Graph, ElementIds.Name, ElementIds.DedicatedUsed, ElementIds.DedicatedTotal, ElementIds.SharedUsed],
                _ => [ElementIds.Download, ElementIds.Upload, ElementIds.DownloadGraph, ElementIds.UploadGraph,
                    ElementIds.Usage, ElementIds.ReceiveLink, ElementIds.TransmitLink, ElementIds.Name, ElementIds.Connection]
            };
            profile.Items.Add(kind, new MetricDisplayOptions { Elements = new HashSet<string>(elements, StringComparer.Ordinal) });
        }
        return profile;
    }

    /// <returns>False only when removing the last selected element of a visible metric.</returns>
    public static bool TrySetElement(MetricDisplayOptions options, string id, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (enabled) options.Elements.Add(id);
        else
        {
            if (options.Visible && options.Elements.Count <= 1 && options.Elements.Contains(id)) return false;
            options.Elements.Remove(id);
        }
        return true;
    }

    public static void Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SchemaVersion = 1;
        if (!Enum.IsDefined(settings.WidgetMode)) settings.WidgetMode = WidgetMode.Compact;
        if (!Enum.IsDefined(settings.Theme)) settings.Theme = ThemeMode.Dark;
        settings.BackgroundTransparency = Math.Clamp(settings.BackgroundTransparency, 0, 100);
        var usageColors = UsageColorPolicy.CreateDefaults();
        foreach (var kind in Enum.GetValues<MetricKind>())
            if (settings.UsageColors is not null && settings.UsageColors.TryGetValue(kind, out var options))
                usageColors[kind] = UsageColorPolicy.NormalizeOptions(options);
        settings.UsageColors = usageColors;
        settings.LockedHeights ??= [];
        foreach (var key in settings.LockedHeights.Keys.ToArray())
        {
            var height = settings.LockedHeights[key];
            if (!Enum.IsDefined(key) || !double.IsFinite(height) || height < 44 || height > 16384)
                settings.LockedHeights.Remove(key);
        }
        settings.DisplayProfiles ??= new DisplayProfiles();
        settings.DisplayProfiles.Compact = NormalizeProfile(settings.DisplayProfiles.Compact, WidgetMode.Compact);
        settings.DisplayProfiles.Expanded = NormalizeProfile(settings.DisplayProfiles.Expanded, WidgetMode.Expanded);
        settings.Placements ??= [];
        foreach (var key in settings.Placements.Keys.ToArray())
        {
            var point = settings.Placements[key];
            if (!Enum.IsDefined(key) || point is null || !PlacementPolicy.IsValidCoordinate(point.Left) || !PlacementPolicy.IsValidCoordinate(point.Top))
                settings.Placements.Remove(key);
            else if (!PlacementPolicy.HasValidMonitorMetadata(point))
            {
                // Older settings only contain absolute coordinates. Partial or malformed
                // monitor metadata must not discard those usable legacy coordinates.
                point.MonitorId = null;
                point.MonitorOffsetX = null;
                point.MonitorOffsetY = null;
            }
        }
        if (string.IsNullOrWhiteSpace(settings.SelectedGpuId)) settings.SelectedGpuId = null;
        if (string.IsNullOrWhiteSpace(settings.SelectedNetworkId)) settings.SelectedNetworkId = null;
    }

    private static DisplayProfile NormalizeProfile(DisplayProfile? existing, WidgetMode mode)
    {
        var defaults = CreateDefaults(mode);
        foreach (var kind in Enum.GetValues<MetricKind>())
        {
            if (existing?.Items is null || !existing.Items.TryGetValue(kind, out var options) || options is null) continue;
            var allowed = Elements(kind).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            var selected = new HashSet<string>(options.Elements ?? [], StringComparer.Ordinal);
            selected.IntersectWith(allowed);
            if (selected.Count == 0) selected = defaults.Items[kind].Elements;
            defaults.Items[kind] = new MetricDisplayOptions { Visible = options.Visible, Elements = selected };
        }
        return defaults;
    }
}
