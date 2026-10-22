namespace ResourceStatus.Core;

/// <summary>Monitor work area in physical screen pixels, including negative virtual desktop origins.</summary>
public sealed record MonitorWorkArea(string Id, int Left, int Top, int Width, int Height, bool IsPrimary);

/// <summary>A temporary on-screen position. It never replaces the user's preferred placement.</summary>
public sealed record PlacementResolution(int Left, int Top, MonitorWorkArea Monitor, bool IsFallback);

public static class PlacementPolicy
{
    public static bool IsValidCoordinate(double value) => double.IsFinite(value) && value >= int.MinValue && value <= int.MaxValue;

    public static bool HasValidMonitorMetadata(WindowPlacement placement) =>
        !string.IsNullOrWhiteSpace(placement.MonitorId) && placement.MonitorId.Length <= 1024 &&
        placement.MonitorId == placement.MonitorId.Trim() && !placement.MonitorId.Any(char.IsControl) &&
        placement.MonitorOffsetX is { } x && IsValidCoordinate(x) && placement.MonitorOffsetY is { } y && IsValidCoordinate(y);

    public static MonitorWorkArea SelectMonitor(WindowPlacement? preferred, IReadOnlyList<MonitorWorkArea> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var available = monitors.Where(IsValidMonitor).ToArray();
        if (available.Length == 0) throw new ArgumentException("At least one valid monitor work area is required.", nameof(monitors));
        var primary = available.FirstOrDefault(monitor => monitor.IsPrimary) ?? available[0];
        if (preferred is null) return primary;
        var exact = available.FirstOrDefault(monitor => string.Equals(monitor.Id, preferred.MonitorId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null && !string.IsNullOrWhiteSpace(preferred.MonitorId)) return exact;
        if (!IsValidCoordinate(preferred.Left) || !IsValidCoordinate(preferred.Top)) return primary;

        // Stable tie-breaking chooses the primary display. Point-to-rectangle distance
        // matches nearest-monitor behavior without overflowing on signed desktop origins.
        MonitorWorkArea closest = primary;
        double closestDistance = DistanceSquared(preferred.Left, preferred.Top, primary);
        foreach (var monitor in available)
        {
            double distance = DistanceSquared(preferred.Left, preferred.Top, monitor);
            if (distance < closestDistance) { closest = monitor; closestDistance = distance; }
        }
        return closest;
    }

    public static PlacementResolution Resolve(WindowPlacement preferred, IReadOnlyList<MonitorWorkArea> monitors, int widthPx, int heightPx)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        if (widthPx <= 0) throw new ArgumentOutOfRangeException(nameof(widthPx));
        if (heightPx <= 0) throw new ArgumentOutOfRangeException(nameof(heightPx));
        var monitor = SelectMonitor(preferred, monitors);
        bool matched = !string.IsNullOrWhiteSpace(preferred.MonitorId) &&
            string.Equals(preferred.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase);
        double x = preferred.Left, y = preferred.Top;
        if (matched && HasValidMonitorMetadata(preferred))
        {
            x = monitor.Left + preferred.MonitorOffsetX!.Value;
            y = monitor.Top + preferred.MonitorOffsetY!.Value;
        }
        // Guard unnormalized caller data as well as persisted settings. All arithmetic
        // stays in double/long until clamped into the native signed-coordinate range.
        if (!double.IsFinite(x)) x = monitor.Left;
        if (!double.IsFinite(y)) y = monitor.Top;
        long maximumX = Math.Max((long)monitor.Left, (long)monitor.Left + monitor.Width - widthPx);
        long maximumY = Math.Max((long)monitor.Top, (long)monitor.Top + monitor.Height - heightPx);
        int left = (int)Math.Clamp(Math.Round(x, MidpointRounding.AwayFromZero), monitor.Left, maximumX);
        int top = (int)Math.Clamp(Math.Round(y, MidpointRounding.AwayFromZero), monitor.Top, maximumY);
        return new PlacementResolution(left, top, monitor, !string.IsNullOrWhiteSpace(preferred.MonitorId) && !matched);
    }

    private static bool IsValidMonitor(MonitorWorkArea monitor) => monitor is not null && !string.IsNullOrWhiteSpace(monitor.Id) &&
        monitor.Width > 0 && monitor.Height > 0 && (long)monitor.Left + monitor.Width - 1 <= int.MaxValue &&
        (long)monitor.Top + monitor.Height - 1 <= int.MaxValue;

    private static double DistanceSquared(double x, double y, MonitorWorkArea monitor)
    {
        double right = (long)monitor.Left + monitor.Width - 1, bottom = (long)monitor.Top + monitor.Height - 1;
        double dx = x - Math.Clamp(x, monitor.Left, right), dy = y - Math.Clamp(y, monitor.Top, bottom);
        return dx * dx + dy * dy;
    }
}
