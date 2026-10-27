namespace ResourceStatus.Core;

public static class UsageColorPolicy
{
    public static IReadOnlyList<MetricKind> SupportedMetrics { get; } = Array.AsReadOnly(new[] { MetricKind.Cpu, MetricKind.Ram, MetricKind.Gpu, MetricKind.Network });
    public const int RecoveryMargin = 3;
    // Very low custom thresholds must still be able to recover at 0%.
    public static double RecoveryThreshold(int threshold) => Math.Max(1, threshold - RecoveryMargin);

    public static Dictionary<MetricKind, UsageColorOptions> CreateDefaults() =>
        SupportedMetrics.ToDictionary(kind => kind, _ => new UsageColorOptions());

    public static bool IsValidThresholds(int warning, int critical) => warning >= 1 && warning < critical && critical <= 100;

    public static UsageSeverity Evaluate(double? value, UsageColorOptions options, UsageSeverity previous = UsageSeverity.Normal)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Evaluate(value, Configuration(options), previous);
    }

    internal readonly record struct Options(bool Enabled, int Warning, int Critical);

    internal static Options Configuration(UsageColorOptions? options) => options is null ? new(true, 80, 95) :
        IsValidThresholds(options.WarningThreshold, options.CriticalThreshold)
            ? new(options.Enabled, options.WarningThreshold, options.CriticalThreshold) : new(options.Enabled, 80, 95);

    internal static UsageColorOptions NormalizeOptions(UsageColorOptions? options)
    {
        var normalized = Configuration(options);
        return new UsageColorOptions { Enabled = normalized.Enabled, WarningThreshold = normalized.Warning, CriticalThreshold = normalized.Critical };
    }

    internal static UsageSeverity Evaluate(double? value, Options options, UsageSeverity previous)
    {
        if (!options.Enabled || value is not { } usage || !double.IsFinite(usage) || usage < 0) return UsageSeverity.Normal;
        usage = Math.Min(100, usage);
        if (usage >= options.Critical || (previous == UsageSeverity.Critical && usage >= RecoveryThreshold(options.Critical)))
            return UsageSeverity.Critical;
        if (usage >= options.Warning || (previous is UsageSeverity.Warning or UsageSeverity.Critical && usage >= RecoveryThreshold(options.Warning)))
            return UsageSeverity.Warning;
        return UsageSeverity.Normal;
    }
}
