using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ResourceStatus.Core;
using ResourceStatus.Native;

namespace ResourceStatus.App;

internal static class Diagnostics
{
    public static Task RunProbeAsync(int seconds, string output) => Task.Run(async () =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var provider = new WindowsMetricsProvider();
        using var process = Process.GetCurrentProcess();
        var startCpu = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        var samples = new List<SystemSnapshot>();
        double maxReadMs = 0, sumReadMs = 0;
        for (int i = 0; i < seconds; i++)
        {
            var readClock = Stopwatch.StartNew();
            var sample = provider.Read(null, null);
            readClock.Stop();
            maxReadMs = Math.Max(maxReadMs, readClock.Elapsed.TotalMilliseconds);
            sumReadMs += readClock.Elapsed.TotalMilliseconds;
            samples.Add(sample);
            if (i % 10 == 0 || i == seconds - 1) Write(i == seconds - 1);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        void Write(bool complete)
        {
            process.Refresh();
            var result = new
            {
                Complete = complete, Count = samples.Count, ElapsedSeconds = clock.Elapsed.TotalSeconds,
                CpuPercentOfMachine = (process.TotalProcessorTime - startCpu).TotalSeconds / Math.Max(.001, clock.Elapsed.TotalSeconds) / Environment.ProcessorCount * 100,
                PrivateMemoryMiB = process.PrivateMemorySize64 / 1048576d,
                AverageReadMs = sumReadMs / samples.Count, MaxReadMs = maxReadMs,
                provider.Gpus, provider.Networks,
                Samples = samples.Count > 15 ? samples.GetRange(samples.Count - 15, 15) : samples
            };
            var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
            File.WriteAllText(output + ".tmp", JsonSerializer.Serialize(result, options));
            File.Move(output + ".tmp", output, true);
        }
    });
}
