using System.Globalization;

namespace ResourceStatus.Core;

public static class Formatting
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    public static string Percent(Reading<double> reading) =>
        Valid(reading) ? Math.Clamp(reading.Value!.Value, 0, 100).ToString("0", Culture) + "%" : "—";

    public static string Bytes(Reading<ulong> reading) => reading.IsReady
        ? Scale(reading.Value!.Value, 1024, ["B", "KiB", "MiB", "GiB", "TiB"]) : "—";

    public static string Rate(Reading<double> reading) => Valid(reading)
        ? Scale(reading.Value!.Value, 1000, ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"]) : "—";

    public static string LinkSpeed(Reading<ulong> reading) => reading.IsReady && reading.Value > 0
        ? Scale(reading.Value!.Value, 1000, ["bps", "Kbps", "Mbps", "Gbps", "Tbps"]) : "—";

    public static string StateLabel(ReadingState state) => state switch
    {
        ReadingState.Ready => "", ReadingState.WarmingUp => "준비 중", ReadingState.Unsupported => "지원 안 됨",
        ReadingState.AccessDenied => "접근 거부", _ => "일시 오류"
    };

    private static bool Valid(Reading<double> reading) => reading.IsReady
        && double.IsFinite(reading.Value!.Value) && reading.Value.Value >= 0;

    private static string Scale(double value, double divisor, string[] units)
    {
        var index = 0;
        while (value >= divisor && index < units.Length - 1) { value /= divisor; index++; }
        var number = value.ToString(index == 0 || value >= 100 ? "0" : "0.#", Culture);
        if (index < units.Length - 1 && double.Parse(number, Culture) >= divisor)
        {   // Rounding reached the next unit, e.g. 1023.95 MiB would print "1024 MiB".
            value /= divisor; index++;
            number = value.ToString(value >= 100 ? "0" : "0.#", Culture);
        }
        return $"{number} {units[index]}";
    }
}
