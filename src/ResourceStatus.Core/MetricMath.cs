using System.Globalization;
using System.Text.RegularExpressions;

namespace ResourceStatus.Core;

public static partial class MetricMath
{
    [GeneratedRegex(@"(?:^|_)luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)_eng_(\d+)(?:_|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GpuInstancePattern();

    /// <summary>Sums process counters for a physical engine, then takes the busiest engine.</summary>
    public static double? AggregateGpuEngines(IEnumerable<KeyValuePair<string, double>> counters, string gpuLuid)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(gpuLuid);
        var engines = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (instance, value) in counters)
        {
            if (!double.IsFinite(value) || value < 0) continue;
            var match = GpuInstancePattern().Match(instance);
            if (!match.Success || !match.Groups[1].ValueSpan.Equals(gpuLuid, StringComparison.OrdinalIgnoreCase)) continue;
            // Engine type names are descriptive; the physical/engine indices identify the hardware engine.
            var key = string.Concat(match.Groups[2].ValueSpan, ":", match.Groups[3].ValueSpan);
            engines[key] = engines.GetValueOrDefault(key) + value;
        }
        return engines.Count == 0 ? null : Math.Clamp(engines.Values.Max(), 0, 100);
    }

    /// <summary>A counter decrease or invalid elapsed interval means a new baseline, not a traffic spike.</summary>
    public static double? NetworkDelta(ulong previousBytes, ulong currentBytes, TimeSpan elapsed)
    {
        var seconds = elapsed.TotalSeconds;
        if (seconds <= 0 || !double.IsFinite(seconds) || currentBytes < previousBytes) return null;
        return (currentBytes - previousBytes) / seconds;
    }

    /// <summary>Duplex links use the greater of the receive/transmit percentages, never their sum.</summary>
    public static double? LinkUsage(double? download, double? upload, ulong receiveBits, ulong transmitBits)
    {
        if (download is null || upload is null || receiveBits == 0 || transmitBits == 0
            || !double.IsFinite(download.Value) || !double.IsFinite(upload.Value) || download < 0 || upload < 0) return null;
        return Math.Clamp(Math.Max(download.Value * 800 / receiveBits, upload.Value * 800 / transmitBits), 0, 100);
    }
}
