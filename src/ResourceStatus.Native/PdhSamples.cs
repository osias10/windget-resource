using ResourceStatus.Core;

namespace ResourceStatus.Native;

internal readonly record struct PdhSample(string Name, uint Status, double Value);

/// <summary>Preserves the selected GPU's error state before discarding invalid counter values.</summary>
internal static class PdhSamples
{
    /// <param name="idleWhenEmpty">The caller verified the adapter exists and the counter read succeeded, so
    /// no matching instance means an idle GPU (e.g. a hybrid laptop's sleeping dGPU), not a missing counter.</param>
    internal static (List<KeyValuePair<string, double>> Values, ReadingState State, string? Detail)
        Select(IEnumerable<PdhSample> samples, string? gpuLuid, bool idleWhenEmpty = false)
    {
        var values = new List<KeyValuePair<string, double>>();
        ReadingState? failure = null;
        string? detail = null;
        var needle = gpuLuid is null ? null : "luid_" + gpuLuid + "_";
        foreach (var sample in samples)
        {
            if (needle is not null && !sample.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            if (sample.Status <= 1 && double.IsFinite(sample.Value) && sample.Value >= 0)
            {
                values.Add(new(sample.Name, sample.Value));
                continue;
            }
            var state = sample.Status <= 1 ? ReadingState.Error : State(sample.Status);
            // Removed process instances must not make the remaining engines unsupported.
            if (failure is null || Priority(state) > Priority(failure.Value))
            {
                failure = state;
                detail = sample.Status <= 1 ? "성능 카운터 값이 유효하지 않습니다." : $"PDH 0x{sample.Status:X8}";
            }
        }
        // Do not publish an underestimated total when one of this GPU's active instances failed.
        if (failure is ReadingState.AccessDenied or ReadingState.Error) return ([], failure.Value, detail);
        // A new process's first sample warms up. Skipping it only underestimates that process for one tick,
        // so valid engines are still published; only an all-warming-up reading is reported as warming up.
        if (values.Count > 0) return (values, ReadingState.Ready, null);
        if (failure is ReadingState.WarmingUp) return ([], failure.Value, detail);
        return failure is null && idleWhenEmpty ? (values, ReadingState.Ready, null)
            : ([], ReadingState.Unsupported, detail ?? "성능 카운터 인스턴스가 없습니다.");
    }

    internal static ReadingState State(uint status) => status switch
    {
        5 or 0xC0000BDB => ReadingState.AccessDenied,
        0xC0000BB8 or 0xC0000BB9 or 0x800007D1 or 0x800007D5 => ReadingState.Unsupported,
        0x800007D3 or 0x800007D4 or 0x800007D6 or 0x800007D7 or 0x800007D8
            or 0xC0000BBA or 0xC0000BC6 => ReadingState.WarmingUp,
        _ => ReadingState.Error
    };

    private static int Priority(ReadingState state) => state switch
    {
        ReadingState.AccessDenied => 4, ReadingState.Error => 3,
        ReadingState.WarmingUp => 2, _ => 1
    };
}

/// <summary>NLM_CONNECTIVITY mapping. Kept in this file because the test project links it.</summary>
internal static class NetworkConnectivity
{
    internal static string Label(bool connected, uint? flags) => !connected ? "연결 끊김"
        : flags is not { } value ? "확인 불가"
        : (value & 0x440) != 0 ? "인터넷 연결"
        // NOTRAFFIC (0x1 IPv4, 0x2 IPv6) alone means no usable connectivity, not a local network.
        : (value & ~3u) != 0 ? "로컬 연결만" : "연결 끊김";
}
