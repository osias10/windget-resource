using ResourceStatus.Core;
using ResourceStatus.Native;
using System.Text.Json;
using System.Net;
using System.Security.Cryptography;
using System.Text;

var cases = new (string Name, Action Run)[]
{
    ("Placement selects the exact monitor identity before absolute coordinates and defaults to primary", () =>
    {
        MonitorWorkArea[] monitors = [new("left-monitor", -1920, 0, 1920, 1040, false), new("main-monitor", 0, 40, 1920, 1040, true)];
        Equal("main-monitor", PlacementPolicy.SelectMonitor(null, monitors).Id);
        Equal("left-monitor", PlacementPolicy.SelectMonitor(new WindowPlacement { Left = -1600, Top = 100 }, monitors).Id);
        Equal("main-monitor", PlacementPolicy.SelectMonitor(new WindowPlacement { Left = 1920, Top = 100 }, monitors).Id);
        var identified = new WindowPlacement { Left = 600, Top = 100, MonitorId = "LEFT-MONITOR", MonitorOffsetX = 75, MonitorOffsetY = 20 };
        var resolved = PlacementPolicy.Resolve(identified, monitors, 240, 170);
        Equal("left-monitor", resolved.Monitor.Id);
        Equal(-1845, resolved.Left);
        Equal(20, resolved.Top);
        Check(!resolved.IsFallback, "Case-insensitive exact identity is authoritative");
    }),
    ("Monitor disconnection temporarily clamps without overwriting the position restored after reconnection", () =>
    {
        var main = new MonitorWorkArea("main", 0, 0, 1920, 1040, true);
        var external = new MonitorWorkArea("external", -1920, 60, 1920, 1020, false);
        var preferred = new WindowPlacement { Left = -1800, Top = 100, MonitorId = "external", MonitorOffsetX = 120, MonitorOffsetY = 40 };
        string before = JsonSerializer.Serialize(preferred);
        var fallback = PlacementPolicy.Resolve(preferred, [main], 240, 170);
        Equal(0, fallback.Left);
        Equal(100, fallback.Top);
        Check(fallback.IsFallback, "Missing preferred monitor is reported as temporary fallback");
        var restored = PlacementPolicy.Resolve(preferred, [main, external], 240, 170);
        Equal(-1800, restored.Left);
        Equal(100, restored.Top);
        Check(!restored.IsFallback, "Reconnected monitor is selected again");
        Equal(before, JsonSerializer.Serialize(preferred));
    }),
    ("Stable monitor identity preserves offsets when primary display and virtual desktop origin change", () =>
    {
        var preferred = new WindowPlacement { Left = -1720, Top = 180, MonitorId = "external", MonitorOffsetX = 200, MonitorOffsetY = 120 };
        var newPrimary = new MonitorWorkArea("external", 0, 40, 2560, 1400, true);
        var oldPrimary = new MonitorWorkArea("main", 2560, 0, 1920, 1040, false);
        var restored = PlacementPolicy.Resolve(preferred, [oldPrimary, newPrimary], 320, 460);
        Equal("external", restored.Monitor.Id);
        Equal(200, restored.Left);
        Equal(160, restored.Top);
        Equal(-1720d, preferred.Left);
        Equal(180d, preferred.Top);
    }),
    ("Temporary resolution and DPI size constraints never replace preferred coordinates", () =>
    {
        var preferred = new WindowPlacement { Left = 1500, Top = 800, MonitorId = "main", MonitorOffsetX = 1500, MonitorOffsetY = 800 };
        var small = new MonitorWorkArea("main", 0, 0, 1280, 720, true);
        var normal = new MonitorWorkArea("main", 0, 0, 1920, 1040, true);
        var constrained = PlacementPolicy.Resolve(preferred, [small], 240, 200);
        Equal(1040, constrained.Left);
        Equal(520, constrained.Top);
        Check(!constrained.IsFallback, "Clamping on the same monitor is not a missing-monitor fallback");
        var scaled = PlacementPolicy.Resolve(preferred, [normal], 480, 400);
        Equal(1440, scaled.Left);
        Equal(640, scaled.Top);
        var restored = PlacementPolicy.Resolve(preferred, [normal], 240, 200);
        Equal(1500, restored.Left);
        Equal(800, restored.Top);
        Equal(1500d, preferred.MonitorOffsetX!.Value);
        Equal(800d, preferred.MonitorOffsetY!.Value);
    }),
    ("Placement supports negative desktop origins and windows larger than their monitor", () =>
    {
        var monitor = new MonitorWorkArea("upper-left", -1920, -1080, 1920, 1040, false);
        var preferred = new WindowPlacement { Left = -1900, Top = -1060, MonitorId = "upper-left", MonitorOffsetX = 20, MonitorOffsetY = 20 };
        var ordinary = PlacementPolicy.Resolve(preferred, [monitor], 240, 170);
        Equal(-1900, ordinary.Left);
        Equal(-1060, ordinary.Top);
        var oversized = PlacementPolicy.Resolve(preferred, [monitor], 3000, 2000);
        Equal(-1920, oversized.Left);
        Equal(-1080, oversized.Top);
        var extreme = new MonitorWorkArea("extreme", int.MinValue, int.MinValue, 100, 100, true);
        var offset = new WindowPlacement { MonitorId = "extreme", MonitorOffsetX = int.MaxValue, MonitorOffsetY = int.MaxValue };
        var bounded = PlacementPolicy.Resolve(offset, [extreme], 1, 1);
        Equal(int.MinValue + 99, bounded.Left);
        Equal(int.MinValue + 99, bounded.Top);
    }),
    ("Mode-specific preferred placements and monitor offsets survive saving during a disconnect", () => WithTemp(directory =>
    {
        string path = Path.Combine(directory, "settings.json");
        var settings = new AppSettings();
        settings.Placements[WidgetMode.Compact] = new WindowPlacement { Left = -1700, Top = 150, MonitorId = "external", MonitorOffsetX = 220, MonitorOffsetY = 150 };
        settings.Placements[WidgetMode.Expanded] = new WindowPlacement { Left = 400, Top = 300, MonitorId = "main", MonitorOffsetX = 400, MonitorOffsetY = 300 };
        var main = new MonitorWorkArea("main", 0, 0, 1920, 1040, true);
        var external = new MonitorWorkArea("external", -1920, 0, 1920, 1040, false);
        var compactFallback = PlacementPolicy.Resolve(settings.Placements[WidgetMode.Compact], [main], 240, 170);
        var expanded = PlacementPolicy.Resolve(settings.Placements[WidgetMode.Expanded], [main], 320, 460);
        Check(compactFallback.IsFallback, "Compact falls back temporarily");
        Check(!expanded.IsFallback && expanded.Left == 400 && expanded.Top == 300, "Expanded remains independent");
        new SettingsStore(path).Save(settings);
        var loaded = new SettingsStore(path).Load();
        var restored = PlacementPolicy.Resolve(loaded.Placements[WidgetMode.Compact], [main, external], 240, 170);
        Equal(-1700, restored.Left);
        Equal(150, restored.Top);
        Equal("external", loaded.Placements[WidgetMode.Compact].MonitorId);
        Equal(220d, loaded.Placements[WidgetMode.Compact].MonitorOffsetX!.Value);
        Equal(400d, loaded.Placements[WidgetMode.Expanded].Left);
    })),
    ("Legacy absolute placements migrate without monitor metadata or coordinate changes", () => WithTemp(directory =>
    {
        string path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, """{"Placements":{"Compact":{"Left":-1800,"Top":65},"Expanded":{"Left":250,"Top":300}},"AlwaysOnTop":false}""");
        var loaded = new SettingsStore(path).Load();
        Equal(-1800d, loaded.Placements[WidgetMode.Compact].Left);
        Equal(65d, loaded.Placements[WidgetMode.Compact].Top);
        Check(loaded.Placements.Values.All(p => p.MonitorId is null && p.MonitorOffsetX is null && p.MonitorOffsetY is null), "Legacy metadata stays absent");
        Check(!loaded.AlwaysOnTop, "Unrelated setting retained");
        var monitor = new MonitorWorkArea("left", -1920, 0, 1920, 1040, false);
        var restored = PlacementPolicy.Resolve(loaded.Placements[WidgetMode.Compact], [monitor], 240, 170);
        Equal(-1800, restored.Left);
        Equal(65, restored.Top);
        Check(!restored.IsFallback, "Legacy coordinates have no missing monitor identity");
    })),
    ("Malformed optional monitor metadata is discarded without losing valid absolute positions", () =>
    {
        WindowPlacement[] malformed = [
            new() { MonitorId = "monitor", MonitorOffsetX = 10 },
            new() { MonitorId = "monitor", MonitorOffsetY = 10 },
            new() { MonitorId = null, MonitorOffsetX = 10, MonitorOffsetY = 20 },
            new() { MonitorId = " ", MonitorOffsetX = 10, MonitorOffsetY = 20 },
            new() { MonitorId = " monitor", MonitorOffsetX = 10, MonitorOffsetY = 20 },
            new() { MonitorId = "monitor\0", MonitorOffsetX = 10, MonitorOffsetY = 20 },
            new() { MonitorId = new string('m', 1025), MonitorOffsetX = 10, MonitorOffsetY = 20 },
            new() { MonitorId = "monitor", MonitorOffsetX = double.NaN, MonitorOffsetY = 20 },
            new() { MonitorId = "monitor", MonitorOffsetX = 10, MonitorOffsetY = double.PositiveInfinity },
            new() { MonitorId = "monitor", MonitorOffsetX = (double)int.MaxValue + 1, MonitorOffsetY = 20 }
        ];
        foreach (var placement in malformed)
        {
            placement.Left = -1700;
            placement.Top = 80;
            var settings = new AppSettings { Placements = { [WidgetMode.Compact] = placement } };
            DisplayCatalog.Normalize(settings);
            Equal(1, settings.Placements.Count);
            Equal(-1700d, placement.Left);
            Equal(80d, placement.Top);
            Check(placement.MonitorId is null && placement.MonitorOffsetX is null && placement.MonitorOffsetY is null, "Only incomplete metadata is cleared");
        }
    }),
    ("Out-of-range native coordinates are rejected while valid signed offsets round-trip", () => WithTemp(directory =>
    {
        foreach (double coordinate in new[] { double.NaN, double.PositiveInfinity, (double)int.MaxValue + 1, (double)int.MinValue - 1 })
        {
            var settings = new AppSettings { Placements = { [WidgetMode.Compact] = new WindowPlacement { Left = coordinate }, [WidgetMode.Expanded] = new WindowPlacement { Top = coordinate } } };
            DisplayCatalog.Normalize(settings);
            Equal(0, settings.Placements.Count);
        }
        var valid = new AppSettings { Placements = { [WidgetMode.Compact] = new WindowPlacement
            { Left = int.MinValue, Top = int.MaxValue, MonitorId = @"\\?\DISPLAY#TEST", MonitorOffsetX = -10, MonitorOffsetY = 25 } } };
        string path = Path.Combine(directory, "settings.json");
        new SettingsStore(path).Save(valid);
        var restored = new SettingsStore(path).Load().Placements[WidgetMode.Compact];
        Equal((double)int.MinValue, restored.Left);
        Equal((double)int.MaxValue, restored.Top);
        Equal(@"\\?\DISPLAY#TEST", restored.MonitorId);
        Equal(-10d, restored.MonitorOffsetX!.Value);
        Equal(25d, restored.MonitorOffsetY!.Value);
    })),
    ("Semantic versions follow numeric prerelease precedence and ignore build metadata", () =>
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.10",
            "1.0.0-beta.999999999999999999999999999999", "1.0.0-rc.1", "1.0.0", "1.0.1-alpha", "1.1.0", "2.0.0"];
        for (int index = 1; index < ordered.Length; index++)
            Check(Semantic(ordered[index]).CompareTo(Semantic(ordered[index - 1])) > 0, ordered[index]);
        Equal(0, Semantic("0.0.3-beta.10+abc.001").CompareTo(Semantic("0.0.3-beta.10+def")));
        Equal(Semantic("0.0.3+sha.1").GetHashCode(), Semantic("0.0.3+sha.2").GetHashCode());
        Equal("0.0.3-beta.10", Semantic("0.0.3-beta.10+commit").PrecedenceText);
        Equal("0.0.3-beta.10+commit", Semantic("0.0.3-beta.10+commit").Text);
    }),
    ("Semantic versions and installer names reject malformed or unsafe suffixes", () =>
    {
        foreach (string version in new[] { "0.0.3-beta.2", "0.0.3-alpha-beta.1", "0.0.3-0", "0.0.3+001.build" })
        {
            Check(ReleaseVersion.TryParse(version, out _), version);
            Check(ReleaseUpdateMetadata.IsInstallerFileName($"ResourceStatus-{version}-win-x64-setup.exe"), version);
        }
        foreach (string version in new[] { "0.0.3-beta.02", "0.0.3-", "0.0.3-alpha..beta", "0.0.3+", "0.0.3+x+y", "0.0.3-beta/1", "0.0.3-beta\\1", "0.0.3-beta%2f1", "0.0.3-beta_1", "0.0.3-beta 1", "0.0.3-beta:1", "0.0.3-β", "00.0.3-beta", "0.0.3.0-beta", "0.0.3-" + new string('a', 129) })
        {
            Check(!ReleaseVersion.TryParse(version, out _), version);
            Check(!ReleaseUpdateMetadata.IsInstallerFileName($"ResourceStatus-{version}-win-x64-setup.exe"), version);
        }
        Check(!ReleaseUpdateMetadata.IsInstallerFileName("ResourceStatus-0.0.3-beta.1-win-x64-setup.exe.bat"), "Executable suffix cannot be appended");
    }),
    ("Prereleases require opt-in even when GitHub flags and semantic suffixes disagree", () =>
    {
        var semanticPrerelease = UpdateFixture("0.0.3-beta.2", prerelease: false);
        Check(ReleaseUpdateMetadata.Parse(semanticPrerelease.Json, new Version(0, 0, 2)) is null, "Suffix excluded by default");
        var included = ReleaseUpdateMetadata.Parse(semanticPrerelease.Json, Semantic("0.0.2"), includePrereleases: true)!;
        Check(included.IsPrerelease && included.SemanticVersion.IsPrerelease, "Suffix remains identifiable");
        Equal("0.0.3-beta.2", included.VersionText);
        var githubPrerelease = UpdateFixture("0.0.3", prerelease: true);
        Check(ReleaseUpdateMetadata.Parse(githubPrerelease.Json, new Version(0, 0, 2)) is null, "GitHub prerelease flag excluded by default");
        Check(ReleaseUpdateMetadata.Parse(githubPrerelease.Json, Semantic("0.0.2"), true)!.IsPrerelease, "Flag-only prerelease identifiable");
        Check(ReleaseUpdateMetadata.Parse(semanticPrerelease.Json.Replace("\"draft\":false", "\"draft\":true"), Semantic("0.0.2"), true) is null, "Opt-in never includes drafts");
    }),
    ("A stable release replaces its prerelease without downgrading a newer prerelease", () =>
    {
        var stable = UpdateFixture("0.0.3");
        Equal("0.0.3", ReleaseUpdateMetadata.Parse(stable.Json, Semantic("0.0.3-beta.10+installed"))!.VersionText);
        Check(ReleaseUpdateMetadata.Parse(stable.Json, Semantic("0.0.4-alpha")) is null, "Turning opt-in off does not downgrade");
        Check(ReleaseUpdateMetadata.Parse(UpdateFixture("0.0.3-beta.2").Json, Semantic("0.0.3-beta.10"), true) is null, "Numeric prerelease downgrade blocked");
        Check(ReleaseUpdateMetadata.Parse(UpdateFixture("0.0.3-beta.10+server").Json, Semantic("0.0.3-beta.10+installed"), true) is null, "Build metadata does not create updates");
    }),
    ("Opt-in scans all trusted pages and selects by semantic version instead of publication order", () =>
    {
        var lower = UpdateFixture("0.0.3-beta.2");
        var newest = UpdateFixture("0.0.3-beta.10");
        var draft = UpdateFixture("9.0.0");
        var requests = new List<Uri>();
        using var client = new ReleaseUpdateClient(new FakeUpdateHandler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            bool first = request.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(first
                ? "[" + lower.Json + "," + draft.Json.Replace("\"draft\":false", "\"draft\":true") + "]"
                : "[" + newest.Json + "," + UpdateFixture("0.0.2").Json + "]") };
            if (first) response.Headers.TryAddWithoutValidation("Link", "<https://evil.test/list?page=2>; rel=\"next\"");
            return Task.FromResult(response);
        }));
        Equal("0.0.3-beta.10", client.CheckAsync(Semantic("0.0.2"), true).GetAwaiter().GetResult()!.VersionText);
        Equal(2, requests.Count);
        Equal(ReleaseUpdateMetadata.ReleasesUrl + "?per_page=100&page=2", requests[1].AbsoluteUri);
        Check(requests.All(uri => uri.Host == "api.github.com"), "Untrusted pagination URL never requested");
    }),
    ("Stable and prerelease check caches are separated by channel and semantic identity", () =>
    {
        int stableRequests = 0, prereleaseRequests = 0;
        var stable = UpdateFixture("0.0.2");
        var prerelease = UpdateFixture("0.0.3-beta.10");
        using var client = new ReleaseUpdateClient(new FakeUpdateHandler((request, _) =>
        {
            bool latest = request.RequestUri!.AbsoluteUri == ReleaseUpdateMetadata.LatestReleaseUrl;
            if (latest) stableRequests++; else prereleaseRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(latest ? stable.Json : "[" + prerelease.Json + "]") });
        }));
        Equal("0.0.2", client.CheckAsync(new Version(0, 0, 1)).GetAwaiter().GetResult()!.VersionText);
        Equal("0.0.3-beta.10", client.CheckAsync(new Version(0, 0, 1), true).GetAwaiter().GetResult()!.VersionText);
        Equal("0.0.2", client.CheckAsync(new Version(0, 0, 1), false).GetAwaiter().GetResult()!.VersionText);
        Equal("0.0.3-beta.10", client.CheckAsync(Semantic("0.0.3-beta.2+first"), true).GetAwaiter().GetResult()!.VersionText);
        Equal("0.0.3-beta.10", client.CheckAsync(Semantic("0.0.3-beta.2+second"), true).GetAwaiter().GetResult()!.VersionText);
        Check(client.CheckAsync(Semantic("0.0.3-beta.10"), true).GetAwaiter().GetResult() is null, "Different installed prerelease is a distinct cache entry");
        Equal(1, stableRequests);
        Equal(3, prereleaseRequests);
    }),
    ("Release lists validate only the chosen newest assets and never silently fall back from a broken newest release", () =>
    {
        var newest = UpdateFixture("0.0.4-beta.1");
        const string incompleteOld = """{"tag_name":"v0.0.3-beta.1","draft":false,"prerelease":true}""";
        Equal("0.0.4-beta.1", ReleaseUpdateMetadata.ParseList("[" + incompleteOld + "," + newest.Json + "]", Semantic("0.0.2"), true)!.VersionText);
        RejectUpdate(() => ReleaseUpdateMetadata.ParseList("[" + newest.Json.Replace("SHA256SUMS.txt", "missing.txt") + "," + UpdateFixture("0.0.3").Json + "]", Semantic("0.0.2"), true));
        Equal("0.0.3", ReleaseUpdateMetadata.ParseList("[" + newest.Json + "," + UpdateFixture("0.0.3").Json + "]", Semantic("0.0.2"))!.VersionText);
    }),
    ("Prerelease pagination is bounded and refuses to report a partially scanned result", () =>
    {
        int requests = 0;
        using var client = new ReleaseUpdateClient(new FakeUpdateHandler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[" + UpdateFixture("0.0.3-beta.1").Json + "]") };
            response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/next>; rel=\"next\"");
            return Task.FromResult(response);
        }));
        RejectUpdate(() => client.CheckAsync(Semantic("0.0.2"), true).GetAwaiter().GetResult());
        Equal(ReleaseUpdateMetadata.MaximumReleasePages, requests);
    }),
    ("Switching update channels cannot bypass GitHub rate limits", () =>
    {
        int requests = 0;
        using var client = new ReleaseUpdateClient(new FakeUpdateHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }));
        RejectUpdate(() => client.CheckAsync(Semantic("0.0.2"), false).GetAwaiter().GetResult());
        RejectUpdate(() => client.CheckAsync(Semantic("0.0.2"), true).GetAwaiter().GetResult());
        Equal(1, requests);
    }),
    ("Canceled prerelease pagination cannot cache an incomplete result", () =>
    {
        using var cancellation = new CancellationTokenSource();
        bool cancelPageTwo = true;
        var newest = UpdateFixture("0.0.4-beta.1");
        using var client = new ReleaseUpdateClient(new FakeUpdateHandler((request, token) =>
        {
            bool first = request.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal);
            if (!first && cancelPageTwo) { cancelPageTwo = false; cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[" + (first ? UpdateFixture("0.0.3-beta.1").Json : newest.Json) + "]") };
            if (first) response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/next>; rel=\"next\"");
            return Task.FromResult(response);
        }));
        try { client.CheckAsync(Semantic("0.0.2"), true, cancellation.Token).GetAwaiter().GetResult(); throw new InvalidOperationException("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Equal("0.0.4-beta.1", client.CheckAsync(Semantic("0.0.2"), true).GetAwaiter().GetResult()!.VersionText);
    }),
    ("Prerelease downloads retain exact safe names and SHA verification including build metadata", () => WithTemp(directory =>
    {
        foreach (string version in new[] { "0.0.3-beta.10", "0.0.3-rc.1+build.001" })
        {
            var fixture = UpdateFixture(version);
            using var client = new ReleaseUpdateClient(UpdateDownloadHandler(fixture));
            string path = client.DownloadAsync(fixture.Update, directory).GetAwaiter().GetResult();
            Equal($"ResourceStatus-{version}-win-x64-setup.exe", Path.GetFileName(path));
            client.VerifyDownloadAsync(path).GetAwaiter().GetResult();
            RejectUpdate(() => ReleaseUpdateMetadata.Parse(fixture.Json.Replace("/osias10/", "/attacker/"), Semantic("0.0.2"), true));
            RejectUpdate(() => ReleaseUpdateMetadata.ReadInstallerChecksum(Encoding.UTF8.GetString(fixture.Manifest).Replace(version, "0.0.3"), fixture.Update.InstallerFileName, fixture.Update.InstallerDigest));
        }
    })),
    ("New update and interaction options default off for fresh and migrated settings", () => WithTemp(directory =>
    {
        var fresh = new AppSettings();
        Check(!fresh.IncludePrereleaseUpdates && !fresh.LockWidgetSize && !fresh.ClickThrough, "Opt-in defaults are safe");
        Equal(0, fresh.LockedHeights.Count);
        string path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, """{"AlwaysOnTop":false,"Theme":"Light","BackgroundTransparency":73}""");
        var loaded = new SettingsStore(path).Load();
        Check(!loaded.IncludePrereleaseUpdates && !loaded.LockWidgetSize && !loaded.ClickThrough, "Migration retains opt-in defaults");
        Check(!loaded.AlwaysOnTop && loaded.Theme == ThemeMode.Light && loaded.BackgroundTransparency == 73, "Existing preferences survive migration");
    })),
    ("Update channel and interaction settings preserve independent saved heights across restart", () => WithTemp(directory =>
    {
        string path = Path.Combine(directory, "settings.json");
        var settings = new AppSettings { IncludePrereleaseUpdates = true, LockWidgetSize = true, ClickThrough = true };
        settings.LockedHeights[WidgetMode.Compact] = 156;
        settings.LockedHeights[WidgetMode.Expanded] = 480;
        new SettingsStore(path).Save(settings);
        var loaded = new SettingsStore(path).Load();
        Check(loaded.IncludePrereleaseUpdates && loaded.LockWidgetSize && loaded.ClickThrough, "Enabled options survive restart");
        Equal(156d, loaded.LockedHeights[WidgetMode.Compact]);
        Equal(480d, loaded.LockedHeights[WidgetMode.Expanded]);
        loaded.LockWidgetSize = loaded.ClickThrough = loaded.IncludePrereleaseUpdates = false;
        new SettingsStore(path).Save(loaded);
        var disabled = new SettingsStore(path).Load();
        Check(!disabled.IncludePrereleaseUpdates && !disabled.LockWidgetSize && !disabled.ClickThrough, "Disabled choices survive restart");
        Equal(156d, disabled.LockedHeights[WidgetMode.Compact]);
        Equal(480d, disabled.LockedHeights[WidgetMode.Expanded]);
    })),
    ("Locked height normalization rejects unsafe heights and unknown modes without changing options", () =>
    {
        foreach (double height in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 0, 43.99, 16384.01 })
        {
            var settings = new AppSettings { LockWidgetSize = true, ClickThrough = true, IncludePrereleaseUpdates = true };
            settings.LockedHeights[WidgetMode.Compact] = height;
            settings.LockedHeights[WidgetMode.Expanded] = 480;
            settings.LockedHeights[(WidgetMode)99] = 156;
            DisplayCatalog.Normalize(settings);
            Equal(1, settings.LockedHeights.Count);
            Equal(480d, settings.LockedHeights[WidgetMode.Expanded]);
            Check(settings.LockWidgetSize && settings.ClickThrough && settings.IncludePrereleaseUpdates, "Unrelated options are retained");
        }
        var limits = new AppSettings { LockedHeights = new() { [WidgetMode.Compact] = 44, [WidgetMode.Expanded] = 16384 } };
        DisplayCatalog.Normalize(limits);
        Equal(2, limits.LockedHeights.Count);
        limits.LockedHeights = null!;
        DisplayCatalog.Normalize(limits);
        Equal(0, limits.LockedHeights.Count);
    }),
    ("Update tags require stable canonical numeric versions", () =>
    {
        foreach (string tag in new[] { "v0.0.2", "v1.10.0", "v12.0.123" })
            Check(ReleaseUpdateMetadata.TryParseTag(tag, out _), tag);
        foreach (string tag in new[] { "1.2.3", "v1.2", "v1.2.3.4", "v1.2.3-beta", "v1.2.3+build", "v01.2.3", "v-1.2.3", "v1. 2.3", "v999999999999.0.0", "v１.2.3" })
            Check(!ReleaseUpdateMetadata.TryParseTag(tag, out _), tag);
    }),
    ("Update comparisons are numeric and ignore assembly revision", () =>
    {
        var fixture = UpdateFixture("0.0.10");
        Equal("0.0.10", ReleaseUpdateMetadata.Parse(fixture.Json, new Version(0, 0, 9, 999))!.VersionText);
        Check(ReleaseUpdateMetadata.Parse(fixture.Json, new Version(0, 0, 10, 123)) is null, "Same release with assembly revision");
        Check(ReleaseUpdateMetadata.Parse(fixture.Json, new Version(0, 1, 0)) is null, "No downgrade");
        Check(ReleaseUpdateMetadata.Parse(fixture.Json.Replace("\"draft\":false", "\"draft\":true"), new Version(0, 0, 0)) is null, "Draft ignored");
        Check(ReleaseUpdateMetadata.Parse(fixture.Json.Replace("\"prerelease\":false", "\"prerelease\":true"), new Version(0, 0, 0)) is null, "Prerelease ignored");
    }),
    ("Update metadata rejects untrusted, missing, duplicate and invalid assets", () =>
    {
        var fixture = UpdateFixture();
        string original = fixture.Update.InstallerUrl.AbsoluteUri;
        foreach (string replacement in new[] {
            original.Replace("https:", "http:"), original.Replace("github.com", "github.com.evil.test"),
            original.Replace("osias10", "other-owner"), original.Replace("/v0.0.2/", "/v0.0.1/"),
            original + "?download=1", original + "#fragment", original.Replace("github.com", "github.com:444"),
            original.Replace("github.com", "attacker@github.com") })
            RejectUpdate(() => ReleaseUpdateMetadata.Parse(fixture.Json.Replace(original, replacement), new Version(0, 0, 0)));
        RejectUpdate(() => ReleaseUpdateMetadata.Parse(fixture.Json.Replace("SHA256SUMS.txt", "other.txt"), new Version(0, 0, 0)));
        RejectUpdate(() => ReleaseUpdateMetadata.Parse(fixture.Json.Replace("\"state\":\"uploaded\"", "\"state\":\"open\""), new Version(0, 0, 0)));
        RejectUpdate(() => ReleaseUpdateMetadata.Parse(fixture.Json.Replace("sha256:", "sha512:"), new Version(0, 0, 0)));
        RejectUpdate(() => ReleaseUpdateMetadata.Parse("[]", new Version(0, 0, 0)));
        using var json = JsonDocument.Parse(fixture.Json);
        string duplicate = fixture.Json[..^2] + "," + json.RootElement.GetProperty("assets")[0].GetRawText() + "]}";
        RejectUpdate(() => ReleaseUpdateMetadata.Parse(duplicate, new Version(0, 0, 0)));
    }),
    ("Update redirects allow only HTTPS GitHub release hosts", () =>
    {
        Uri original = UpdateFixture().Update.InstallerUrl;
        Check(ReleaseUpdateMetadata.IsTrustedAssetRedirect(original, original), "Original URL");
        Check(ReleaseUpdateMetadata.IsTrustedAssetRedirect(new Uri("https://release-assets.githubusercontent.com/asset?token=abc"), original), "Current release CDN");
        Check(ReleaseUpdateMetadata.IsTrustedAssetRedirect(new Uri("https://objects.githubusercontent.com/asset?token=abc"), original), "Release object CDN");
        foreach (string url in new[] { "http://release-assets.githubusercontent.com/asset", "https://release-assets.githubusercontent.com.evil.test/asset", "https://github.com/other/repo/asset", "https://raw.githubusercontent.com/asset", "https://user@objects.githubusercontent.com/asset", "https://objects.githubusercontent.com:444/asset", "https://objects.githubusercontent.com/asset#fragment" })
            Check(!ReleaseUpdateMetadata.IsTrustedAssetRedirect(new Uri(url), original), url);
    }),
    ("Update checksum parser rejects missing, duplicate, malformed or conflicting checksums", () =>
    {
        var fixture = UpdateFixture();
        string digest = fixture.Update.InstallerDigest!;
        string name = fixture.Update.InstallerFileName;
        Equal(digest, ReleaseUpdateMetadata.ReadInstallerChecksum($"\uFEFF{digest.ToUpperInvariant()} *{name}\r\n", name, digest));
        Equal(digest, ReleaseUpdateMetadata.ReadInstallerChecksum(Encoding.UTF8.GetString(fixture.Manifest), name, null));
        foreach (string manifest in new[] { "", $"{digest}  other.exe", $"wrong  {name}", $"{digest}  {name}\n{digest}  {name}", $"{digest}  ../{name}", $"{new string('0', 64)}  {name}" })
            RejectUpdate(() => ReleaseUpdateMetadata.ReadInstallerChecksum(manifest, name, digest));
    }),
    ("Update checks cache success and rate limit failures without repeated requests", () =>
    {
        var fixture = UpdateFixture();
        int requests = 0;
        using var client = new ReleaseUpdateClient(new FakeUpdateHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fixture.Json) });
        }));
        Equal("0.0.2", client.CheckAsync(new Version(0, 0, 1)).GetAwaiter().GetResult()!.VersionText);
        Equal("0.0.2", client.CheckAsync(new Version(0, 0, 1, 999)).GetAwaiter().GetResult()!.VersionText);
        Equal(1, requests);
        int limitedRequests = 0;
        using var limited = new ReleaseUpdateClient(new FakeUpdateHandler((_, _) =>
        {
            limitedRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }));
        for (int i = 0; i < 2; i++) RejectUpdate(() => limited.CheckAsync(new Version(0, 0, 1)).GetAwaiter().GetResult());
        Equal(1, limitedRequests);
    }),
    ("Update check bounds metadata and never follows API redirects", () =>
    {
        using var oversized = new ReleaseUpdateClient(new FakeUpdateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[ReleaseUpdateMetadata.MaximumMetadataSize + 1]) })));
        RejectUpdate(() => oversized.CheckAsync(new Version(0, 0, 1)).GetAwaiter().GetResult());
        int requests = 0;
        using var redirected = new ReleaseUpdateClient(new FakeUpdateHandler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.test/release");
            return Task.FromResult(response);
        }));
        RejectUpdate(() => redirected.CheckAsync(new Version(0, 0, 1)).GetAwaiter().GetResult());
        Equal(1, requests);
    }),
    ("Closing the app during an update check preserves cancellation without a semaphore dispose race", () =>
    {
        using var cancellation = new CancellationTokenSource();
        var client = new ReleaseUpdateClient(new FakeUpdateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("A cancelled request must not complete");
        }));
        Task<ReleaseUpdate?> check = client.CheckAsync(new Version(0, 0, 1), cancellation.Token);
        cancellation.Cancel();
        client.Dispose();
        try { check.GetAwaiter().GetResult(); throw new InvalidOperationException("Expected cancellation"); }
        catch (OperationCanceledException) { }
    }),
    ("Update download verifies exact bytes, atomically finalizes and rechecks before install", () => WithTemp(directory =>
    {
        var fixture = UpdateFixture();
        var progress = new List<double>();
        using var client = new ReleaseUpdateClient(UpdateDownloadHandler(fixture));
        string path = client.DownloadAsync(fixture.Update, directory, new ImmediateProgress(progress.Add)).GetAwaiter().GetResult();
        Check(Path.IsPathFullyQualified(path) && Path.GetFileName(path) == fixture.Update.InstallerFileName, "Exact final installer path");
        Check(File.ReadAllBytes(path).SequenceEqual(fixture.Installer), "Verified bytes");
        Equal(0, Directory.GetFiles(directory, "*.partial", SearchOption.AllDirectories).Length);
        Equal(0d, progress.First());
        Equal(100d, progress.Last());
        Check(progress.All(x => x >= 0 && x <= 100), "Progress range");
        client.VerifyDownloadAsync(path).GetAwaiter().GetResult();
        File.WriteAllBytes(path, [9, 9, 9, 9]);
        RejectUpdate(() => client.VerifyDownloadAsync(path).GetAwaiter().GetResult());
        RejectUpdate(() => client.VerifyDownloadAsync(Path.Combine(directory, "unknown.exe")).GetAwaiter().GetResult());
    })),
    ("Update download supports validated CDN redirects and refuses an untrusted redirect", () => WithTemp(directory =>
    {
        var fixture = UpdateFixture();
        int cdnRequests = 0;
        using var trusted = new ReleaseUpdateClient(new FakeUpdateHandler((request, _) =>
        {
            if (request.RequestUri == fixture.Update.ChecksumsUrl) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Manifest) });
            if (request.RequestUri!.Host == "release-assets.githubusercontent.com")
            {
                cdnRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Installer) });
            }
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/test-asset?token=abc");
            return Task.FromResult(response);
        }));
        Check(File.Exists(trusted.DownloadAsync(fixture.Update, directory).GetAwaiter().GetResult()), "Redirect download finished");
        Equal(1, cdnRequests);
        int requests = 0;
        using var untrusted = new ReleaseUpdateClient(new FakeUpdateHandler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.test/asset");
            return Task.FromResult(response);
        }));
        RejectUpdate(() => untrusted.DownloadAsync(fixture.Update, directory).GetAwaiter().GetResult());
        Equal(1, requests);
    })),
    ("Update corrupted and truncated downloads leave no executable or temporary file", () => WithTemp(directory =>
    {
        var fixture = UpdateFixture();
        foreach (byte[] invalid in new byte[][] { [9, 9, 9, 9], [1, 2], [1, 2, 3, 4, 5] })
        {
            using var client = new ReleaseUpdateClient(UpdateDownloadHandler(fixture, invalid));
            RejectUpdate(() => client.DownloadAsync(fixture.Update, directory).GetAwaiter().GetResult());
            Equal(0, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
            Equal(0, Directory.GetDirectories(directory).Length);
        }
    })),
    ("Update cancellation removes partial download and preserves cancellation semantics", () => WithTemp(directory =>
    {
        var fixture = UpdateFixture();
        using var cancellation = new CancellationTokenSource();
        using var client = new ReleaseUpdateClient(UpdateDownloadHandler(fixture));
        try
        {
            client.DownloadAsync(fixture.Update, directory, new ImmediateProgress(value => { if (value > 0) cancellation.Cancel(); }), cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("Cancellation was ignored");
        }
        catch (OperationCanceledException) { }
        Equal(0, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
        Equal(0, Directory.GetDirectories(directory).Length);
    })),
    ("Update missing API digest still requires a valid published checksum", () => WithTemp(directory =>
    {
        var fixture = UpdateFixture();
        string json = fixture.Json.Replace($"\"sha256:{fixture.Update.InstallerDigest}\"", "null");
        var withoutDigest = ReleaseUpdateMetadata.Parse(json, new Version(0, 0, 0))!;
        using var client = new ReleaseUpdateClient(UpdateDownloadHandler(fixture));
        Check(File.Exists(client.DownloadAsync(withoutDigest, directory).GetAwaiter().GetResult()), "Manifest provides required SHA-256");
    })),
    ("PDH skips warming-up instances when valid engine values exist", () =>
    {
        const string luid = "0x00000000_0x00000123";
        var name = $"pid_1_luid_{luid}_phys_0_eng_0_engtype_3D";
        var result = PdhSamples.Select([new(name, 0, 15), new($"pid_2_luid_{luid}_phys_0_eng_0_engtype_3D", 0xC0000BBA, 0),
            new($"pid_3_luid_{luid}_phys_0_eng_1_engtype_Copy", 0x800007D6, 0)], luid);
        Equal(ReadingState.Ready, result.State);
        Equal(name, result.Values.Single().Key);
        Equal(15d, result.Values.Single().Value);
        Equal(ReadingState.Error, PdhSamples.Select([new(name, 0, 15), new(name + "#1", 0xC0000BBA, 0), new(name + "#2", 0xC0000BBC, 0)], luid).State);
        Equal(ReadingState.AccessDenied, PdhSamples.Select([new(name, 0, 15), new(name + "#1", 0xC0000BDB, 0)], luid).State);
    }),
    ("PDH reports an unmatched but known adapter as idle only when requested", () =>
    {
        const string luid = "0x00000000_0x00000123", other = "0x00000000_0x00000456";
        PdhSample[] samples = [new($"pid_1_luid_{other}_phys_0_eng_0_engtype_3D", 0, 40)];
        Equal(ReadingState.Unsupported, PdhSamples.Select(samples, luid).State);
        var idle = PdhSamples.Select(samples, luid, idleWhenEmpty: true);
        Equal(ReadingState.Ready, idle.State);
        Equal(0, idle.Values.Count);
        Equal(ReadingState.WarmingUp, PdhSamples.Select([new($"pid_1_luid_{luid}_phys_0_eng_0_engtype_3D", 0xC0000BBA, 0)], luid, true).State);
        Equal(ReadingState.Error, PdhSamples.Select([new($"pid_1_luid_{luid}_phys_0_eng_0_engtype_3D", 0xC0000BBC, 0)], luid, true).State);
    }),
    ("Network connectivity ignores NOTRAFFIC-only flags", () =>
    {
        Equal("연결 끊김", NetworkConnectivity.Label(true, 0x1));
        Equal("연결 끊김", NetworkConnectivity.Label(true, 0x3));
        Equal("연결 끊김", NetworkConnectivity.Label(true, 0));
        Equal("로컬 연결만", NetworkConnectivity.Label(true, 0x2 | 0x20));
        Equal("인터넷 연결", NetworkConnectivity.Label(true, 0x1 | 0x400));
        Equal("인터넷 연결", NetworkConnectivity.Label(true, 0x40));
        Equal("확인 불가", NetworkConnectivity.Label(true, null));
        Equal("연결 끊김", NetworkConnectivity.Label(false, 0x40));
    }),
    ("PDH distinguishes missing GPU instances from valid idle zero", () =>
    {
        const string luid = "0x00000000_0x00000123";
        var missing = PdhSamples.Select([], luid);
        Equal(ReadingState.Unsupported, missing.State);
        Equal(0, missing.Values.Count);
        var idle = PdhSamples.Select([new($"pid_1_luid_{luid}_phys_0_eng_0_engtype_3D", 0, 0)], luid);
        Equal(ReadingState.Ready, idle.State);
        Equal(0d, idle.Values.Single().Value);
    }),
    ("PDH selects adapter before reducing per-instance failures", () =>
    {
        const string selected = "0x00000000_0x00000123", other = "0x00000000_0x00000456";
        PdhSample[] samples = [new($"pid_1_luid_{selected}_phys_0_eng_0_engtype_3D", 0xC0000BDB, 0),
            new($"pid_2_luid_{other}_phys_0_eng_0_engtype_3D", 0, 27)];
        Equal(ReadingState.AccessDenied, PdhSamples.Select(samples, selected).State);
        var readable = PdhSamples.Select(samples, other);
        Equal(ReadingState.Ready, readable.State);
        Equal(27d, readable.Values.Single().Value);
    }),
    ("PDH does not publish partial totals when a selected engine fails", () =>
    {
        const string luid = "0x00000000_0x00000123";
        PdhSample[] samples = [new($"pid_1_luid_{luid}_phys_0_eng_0_engtype_3D", 0, 20),
            new($"pid_2_luid_{luid}_phys_0_eng_0_engtype_3D", 0xC0000BBC, 0)];
        var result = PdhSamples.Select(samples, luid);
        Equal(ReadingState.Error, result.State);
        Equal(0, result.Values.Count);
    }),
    ("PDH new instances warm up and disappearing instances do not poison valid totals", () =>
    {
        const string luid = "0x00000000_0x00000123";
        var name = $"pid_1_luid_{luid}_phys_0_eng_0_engtype_3D";
        Equal(ReadingState.WarmingUp, PdhSamples.Select([new(name, 0xC0000BBA, 0)], luid).State);
        Equal(ReadingState.Ready, PdhSamples.Select([new(name, 1, 10), new(name + "#1", 0x800007D1, 0)], luid).State);
        Equal(ReadingState.Error, PdhSamples.Select([new(name, 0, double.NaN)], luid).State);
        Equal(ReadingState.Error, PdhSamples.Select([new(name, 0, -1)], luid).State);
        Equal(ReadingState.Unsupported, PdhSamples.State(0x800007D5));
    }),
    ("Compact and expanded defaults are independent", () =>
    {
        var settings = new AppSettings();
        var compact = settings.DisplayProfiles.Compact;
        var expanded = settings.DisplayProfiles.Expanded;
        Check(compact.Items[MetricKind.Cpu].Has(ElementIds.Gauge), "Compact CPU gauge default");
        Check(!compact.Items[MetricKind.Cpu].Has(ElementIds.Graph), "Compact CPU graph initially hidden");
        Check(expanded.Items[MetricKind.Cpu].Has(ElementIds.Graph), "Expanded CPU graph default");
        compact.Items[MetricKind.Cpu].Elements.Clear();
        Check(expanded.Items[MetricKind.Cpu].Has(ElementIds.Usage), "Expanded unchanged");
        Check(new AppSettings().DisplayProfiles.Compact.Items[MetricKind.Cpu].Has(ElementIds.Usage), "Fresh defaults unchanged");
    }),
    ("Every approved element exists exactly once", () =>
    {
        var expected = new Dictionary<MetricKind, int> { [MetricKind.Cpu] = 4, [MetricKind.Ram] = 5, [MetricKind.Gpu] = 7, [MetricKind.Network] = 10 };
        foreach (var (kind, count) in expected)
        {
            var elements = DisplayCatalog.Elements(kind);
            Equal(count, elements.Count);
            Equal(count, elements.Select(x => x.Id).Distinct().Count());
            foreach (var mode in Enum.GetValues<WidgetMode>())
                Check(DisplayCatalog.CreateDefaults(mode).Items[kind].Elements.IsSubsetOf(elements.Select(x => x.Id)), "Defaults must be selectable");
        }
    }),
    ("Visible metric cannot lose its last element", () =>
    {
        var options = new MetricDisplayOptions { Elements = [ElementIds.Graph] };
        Check(!DisplayCatalog.TrySetElement(options, ElementIds.Graph, false), "Last graph must be preserved");
        Check(options.Has(ElementIds.Graph), "Rejected change leaves model intact");
        Check(DisplayCatalog.TrySetElement(options, ElementIds.Usage, true), "Second element accepted");
        Check(DisplayCatalog.TrySetElement(options, ElementIds.Graph, false), "Can now remove graph");
        Check(DisplayCatalog.TrySetElement(options, ElementIds.Graph, false), "Removing absent key is harmless");
    }),
    ("Hidden metrics preserve choices and all metrics may be hidden", () =>
    {
        var settings = new AppSettings();
        foreach (var options in settings.DisplayProfiles.Compact.Items.Values) options.Visible = false;
        DisplayCatalog.Normalize(settings);
        Check(settings.DisplayProfiles.Compact.Items.Values.All(x => !x.Visible), "All hidden persisted");
        Check(settings.DisplayProfiles.Compact.Items[MetricKind.Gpu].Has(ElementIds.Gauge), "Hidden selection retained");
        settings.DisplayProfiles.Compact.Items[MetricKind.Gpu].Visible = true;
        Check(settings.DisplayProfiles.Compact.Items[MetricKind.Gpu].Has(ElementIds.Usage), "Reappears with original choice");
    }),
    ("Normalize repairs invalid options, missing metrics, and aliases", () =>
    {
        var shared = new DisplayProfile { Items = { [MetricKind.Cpu] = new MetricDisplayOptions { Visible = false, Elements = ["future", ElementIds.Graph] } } };
        var settings = new AppSettings { WidgetMode = (WidgetMode)99, DisplayProfiles = new DisplayProfiles { Compact = shared, Expanded = shared } };
        settings.Placements[WidgetMode.Compact] = new WindowPlacement { Left = double.NaN, Top = 2 };
        DisplayCatalog.Normalize(settings);
        Equal(WidgetMode.Compact, settings.WidgetMode);
        Equal(4, settings.DisplayProfiles.Compact.Items.Count);
        Check(!settings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Has("future"), "Unknown element removed");
        Check(!settings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Visible, "Hidden state retained");
        settings.DisplayProfiles.Compact.Items[MetricKind.Cpu].Elements.Add(ElementIds.Usage);
        Check(!settings.DisplayProfiles.Expanded.Items[MetricKind.Cpu].Has(ElementIds.Usage), "Shared profile input is detached");
        Equal(0, settings.Placements.Count);
    }),
    ("Settings survive save, replacement, restart and missing parent directory", () => WithTemp(directory =>
    {
        var path = Path.Combine(directory, "nested", "settings.json");
        var store = new SettingsStore(path);
        var settings = store.Load();
        settings.WidgetMode = WidgetMode.Expanded;
        settings.SelectedGpuId = "gpu-一";
        settings.SelectedNetworkId = "ethernet";
        settings.DisplayProfiles.Compact.Items[MetricKind.Cpu] = new MetricDisplayOptions { Elements = [ElementIds.Graph] };
        settings.DisplayProfiles.Expanded.Items[MetricKind.Network].Visible = false;
        settings.Placements[WidgetMode.Compact] = new WindowPlacement { Left = -1920, Top = 32 };
        store.Save(settings);
        settings.AlwaysOnTop = false;
        store.Save(settings);
        var restored = new SettingsStore(path).Load();
        Equal(WidgetMode.Expanded, restored.WidgetMode);
        Equal("gpu-一", restored.SelectedGpuId);
        Check(!restored.AlwaysOnTop, "Atomic replacement saved second value");
        Check(restored.DisplayProfiles.Compact.Items[MetricKind.Cpu].Elements.SetEquals([ElementIds.Graph]), "Graph-only saved");
        Check(!restored.DisplayProfiles.Expanded.Items[MetricKind.Network].Visible, "Visibility saved");
        Near(-1920, restored.Placements[WidgetMode.Compact].Left);
        Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length);
    })),
    ("Existing settings without a theme preserve the dark appearance and preferences", () => WithTemp(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, """{"WidgetMode":"Expanded","AlwaysOnTop":false,"SelectedNetworkId":"ethernet"}""");
        var loaded = new SettingsStore(path).Load();
        Equal(ThemeMode.Dark, loaded.Theme);
        Equal(WidgetMode.Expanded, loaded.WidgetMode);
        Check(!loaded.AlwaysOnTop, "Legacy preferences retained");
        Equal("ethernet", loaded.SelectedNetworkId);
    })),
    ("All three theme choices survive replacement and restart", () => WithTemp(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);
        foreach (var mode in Enum.GetValues<ThemeMode>())
        {
            store.Save(new AppSettings { Theme = mode, SelectedGpuId = "theme-test-gpu" });
            var loaded = new SettingsStore(path).Load();
            Equal(mode, loaded.Theme);
            Equal("theme-test-gpu", loaded.SelectedGpuId);
        }
    })),
    ("Invalid theme values recover dark without discarding other preferences", () => WithTemp(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, """{"Theme":999,"AlwaysOnTop":false,"WidgetMode":"Expanded"}""");
        var loaded = new SettingsStore(path).Load();
        Equal(ThemeMode.Dark, loaded.Theme);
        Check(!loaded.AlwaysOnTop, "Valid preferences survive an unknown theme");
        Equal(WidgetMode.Expanded, loaded.WidgetMode);
        loaded.Theme = (ThemeMode)(-1);
        DisplayCatalog.Normalize(loaded);
        Equal(ThemeMode.Dark, loaded.Theme);
    })),
    ("Existing settings receive the default transparency without enabling a disabled background", () => WithTemp(directory =>
    {
        Equal(25, new AppSettings().BackgroundTransparency);
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, """{"Transparency":false,"Theme":"Light","WidgetMode":"Expanded"}""");
        var loaded = new SettingsStore(path).Load();
        Equal(25, loaded.BackgroundTransparency);
        Check(!loaded.Transparency, "Disabled transparency remains disabled when upgrading");
        Equal(ThemeMode.Light, loaded.Theme);
        Equal(WidgetMode.Expanded, loaded.WidgetMode);
    })),
    ("Transparency endpoints and disabled choices survive save and restart", () => WithTemp(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);
        foreach (int value in new[] { 0, 25, 67, 100 })
        {
            var settings = new AppSettings { BackgroundTransparency = value, Transparency = false, Theme = ThemeMode.System };
            store.Save(settings);
            var restored = new SettingsStore(path).Load();
            Equal(value, restored.BackgroundTransparency);
            Check(!restored.Transparency, "Saving a choice does not enable the background effect");
            Equal(ThemeMode.System, restored.Theme);
            restored.Transparency = true;
            store.Save(restored);
            var enabled = new SettingsStore(path).Load();
            Equal(value, enabled.BackgroundTransparency);
            Check(enabled.Transparency, "Enabling the effect preserves the selected percentage");
        }
    })),
    ("Out of range transparency is clamped while valid settings are preserved", () => WithTemp(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        foreach (var (stored, expected) in new[] { (int.MinValue, 0), (-1, 0), (101, 100), (int.MaxValue, 100) })
        {
            File.WriteAllText(path, $"{{\"BackgroundTransparency\":{stored},\"Transparency\":false,\"AlwaysOnTop\":false}}");
            var loaded = new SettingsStore(path).Load();
            Equal(expected, loaded.BackgroundTransparency);
            Check(!loaded.Transparency && !loaded.AlwaysOnTop, "Unrelated preferences remain intact");
        }
        var direct = new AppSettings { BackgroundTransparency = 250 };
        DisplayCatalog.Normalize(direct);
        Equal(100, direct.BackgroundTransparency);
    })),
    ("Unknown JSON fields are ignored and partial profiles retain defaults", () => WithTemp(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, """{"futureVersionFeature":{"new":true},"AlwaysOnTop":false,"DisplayProfiles":{"Compact":{"Items":{"Cpu":{"Visible":false,"Elements":["graph","future"]}}}}}""");
        var loaded = new SettingsStore(path).Load();
        Check(!loaded.AlwaysOnTop, "Known field loaded");
        Equal(4, loaded.DisplayProfiles.Compact.Items.Count);
        Check(loaded.DisplayProfiles.Compact.Items[MetricKind.Cpu].Elements.SetEquals([ElementIds.Graph]), "Unknown element omitted");
        Check(!loaded.DisplayProfiles.Compact.Items[MetricKind.Cpu].Visible, "Hidden retained");
        Check(loaded.DisplayProfiles.Expanded.Items[MetricKind.Network].Has(ElementIds.DownloadGraph), "Expanded defaults available");
    })),
    ("Malformed JSON receives a recoverable backup", () => WithTemp(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "{ broken file");
        var store = new SettingsStore(path);
        Equal(WidgetMode.Compact, store.Load().WidgetMode);
        var backups = Directory.GetFiles(directory, "settings.json.corrupt-*.json");
        Equal(1, backups.Length);
        Equal("{ broken file", File.ReadAllText(backups[0]));
        store.Save(new AppSettings());
        Check(store.Load().AlwaysOnTop, "Default settings can replace corruption");
    })),
    ("Null profiles and empty selections recover safe defaults", () => WithTemp(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, """{"Placements":null,"DisplayProfiles":{"Compact":null,"Expanded":{"Items":{"Cpu":{"Visible":true,"Elements":[]},"Ram":null,"Gpu":{"Elements":null}}}}}""");
        var settings = new SettingsStore(path).Load();
        foreach (var mode in Enum.GetValues<WidgetMode>())
            Check(settings.DisplayProfiles.For(mode).Items.Values.All(x => x.Elements.Count > 0), "No blank visible metric");
        Equal(0, settings.Placements.Count);
    })),
    ("A failed settings serialization preserves the last saved file", () => WithTemp(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings { AlwaysOnTop = false });
        var before = File.ReadAllText(path);
        var invalid = new AppSettings { Placements = { [WidgetMode.Compact] = new WindowPlacement { Left = double.NaN } } };
        var failed = false;
        try { store.Save(invalid); } catch (ArgumentException) { failed = true; }
        Check(failed, "Invalid float save rejected");
        Equal(before, File.ReadAllText(path));
        Equal(0, Directory.GetFiles(directory, "*.tmp").Length);
    })),
    ("History is time based and preserves real zeros", () =>
    {
        var buffer = new HistoryBuffer();
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < 100; i++) buffer.Add(Snapshot(start.AddSeconds(i), i == 99 ? 0 : 25));
        Equal(61, buffer.Samples.Count);
        Equal(start.AddSeconds(39), buffer.Samples[0].Timestamp);
        Near(0, buffer.Samples[^1].Cpu!.Value);
    }),
    ("History never turns failed readings into zeros", () =>
    {
        var buffer = new HistoryBuffer();
        var timestamp = DateTimeOffset.UtcNow;
        var missing = Snapshot(timestamp) with { Cpu = new CpuSnapshot("CPU", Reading<double>.Missing(ReadingState.Unsupported)) };
        buffer.Add(missing);
        Check(buffer.Samples[0].Cpu is null, "Missing CPU is a graph gap");
        var nan = Snapshot(timestamp.AddSeconds(1)) with { Cpu = new CpuSnapshot("CPU", Reading<double>.Ready(double.NaN)) };
        buffer.Add(nan);
        Check(buffer.Samples[^1].Cpu is null, "Non-finite value is a graph gap");
    }),
    ("History marks pauses and tracks device identities", () =>
    {
        var buffer = new HistoryBuffer();
        var start = DateTimeOffset.UtcNow;
        buffer.Add(Snapshot(start));
        buffer.Add(Snapshot(start.AddSeconds(12)) with { Gpu = Snapshot(start).Gpu with { DeviceId = "gpu2" } });
        Equal(3, buffer.Samples.Count);
        Check(buffer.Samples[1].Cpu is null && buffer.Samples[1].Download is null, "Pause has a missing marker");
        Equal("gpu2", buffer.Samples[2].GpuId);
        buffer.Add(Snapshot(start.AddSeconds(90)));
        Equal(1, buffer.Samples.Count);
    }),
    ("History replaces duplicate timestamps and ignores stale snapshots", () =>
    {
        var buffer = new HistoryBuffer();
        var now = DateTimeOffset.UtcNow;
        buffer.Add(Snapshot(now, 1));
        buffer.Add(Snapshot(now, 2));
        buffer.Add(Snapshot(now.AddSeconds(-1), 99));
        Equal(1, buffer.Samples.Count);
        Near(2, buffer.Samples[0].Cpu!.Value);
    }),
    ("Network rate uses actual elapsed time without unsigned underflow", () =>
    {
        Near(200, MetricMath.NetworkDelta(100, 400, TimeSpan.FromSeconds(1.5))!.Value);
        Near(0, MetricMath.NetworkDelta(100, 100, TimeSpan.FromSeconds(1))!.Value);
        Check(MetricMath.NetworkDelta(400, 100, TimeSpan.FromSeconds(1)) is null, "Reset counter is a missing baseline");
        Check(MetricMath.NetworkDelta(100, 400, TimeSpan.Zero) is null, "No elapsed interval");
        Check(MetricMath.NetworkDelta(100, 400, TimeSpan.FromSeconds(-1)) is null, "Backward clock ignored");
        Near(10, MetricMath.NetworkDelta(ulong.MaxValue - 10, ulong.MaxValue, TimeSpan.FromSeconds(1))!.Value);
    }),
    ("Duplex network usage uses the busiest direction and actual link speeds", () =>
    {
        Near(50, MetricMath.LinkUsage(62_500_000, 25_000_000, 1_000_000_000, 1_000_000_000)!.Value);
        Near(80, MetricMath.LinkUsage(1_000_000, 10_000_000, 1_000_000_000, 100_000_000)!.Value);
        Near(100, MetricMath.LinkUsage(500_000_000, 0, 1_000_000_000, 1_000_000_000)!.Value);
        Check(MetricMath.LinkUsage(100, 200, 0, 1_000_000) is null, "Unknown link denominator");
        Check(MetricMath.LinkUsage(null, 200, 1_000_000, 1_000_000) is null, "Warmup rate");
        Check(MetricMath.LinkUsage(double.NaN, 200, 1_000_000, 1_000_000) is null, "Invalid sample");
    }),
    ("GPU sums processes on each engine and chooses the busiest engine", () =>
    {
        var values = new Dictionary<string, double>
        {
            ["pid_10_luid_0x00000000_0x0000ABCD_phys_0_eng_0_engtype_3D"] = 30,
            ["pid_11_luid_0x00000000_0x0000abcd_phys_0_eng_0_engtype_3D"] = 20,
            ["pid_10_luid_0x00000000_0x0000abcd_phys_0_eng_1_engtype_Copy"] = 12,
            ["pid_11_luid_0x00000000_0x0000abcd_phys_1_eng_0_engtype_3D"] = 40,
            ["pid_12_luid_0x00000000_0x0000ffff_phys_0_eng_0_engtype_3D"] = 99,
            ["invalid_instance"] = 99
        };
        Near(50, MetricMath.AggregateGpuEngines(values, "0x00000000_0x0000abcd")!.Value);
        Check(MetricMath.AggregateGpuEngines(values, "0x00000000_0x00001234") is null, "Missing GPU is not idle zero");
    }),
    ("GPU invalid counters are skipped and aggregate overshoot is clamped", () =>
    {
        var values = new Dictionary<string, double>
        {
            ["pid_1_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D"] = 80,
            ["pid_2_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D"] = 80,
            ["pid_3_luid_0x00000000_0x00000001_phys_0_eng_1_engtype_Copy"] = double.NaN,
            ["pid_4_luid_0x00000000_0x00000001_phys_0_eng_2_engtype_Copy"] = -1
        };
        Near(100, MetricMath.AggregateGpuEngines(values, "0x00000000_0x00000001")!.Value);
        Check(MetricMath.AggregateGpuEngines([], "0x00000000_0x00000001") is null, "No engine counters");
    }),
    ("Formatting distinguishes binary memory, decimal traffic and missing samples", () =>
    {
        Equal("32 GiB", Formatting.Bytes(Reading<ulong>.Ready(32UL * 1024 * 1024 * 1024)));
        Equal("12.4 MB/s", Formatting.Rate(Reading<double>.Ready(12_400_000)));
        Equal("820 KB/s", Formatting.Rate(Reading<double>.Ready(820_000)));
        Equal("1 Gbps", Formatting.LinkSpeed(Reading<ulong>.Ready(1_000_000_000)));
        Equal("24%", Formatting.Percent(Reading<double>.Ready(24.1)));
        Equal("0%", Formatting.Percent(Reading<double>.Ready(0)));
        Equal("—", Formatting.Percent(Reading<double>.Missing(ReadingState.Error)));
        Equal("—", Formatting.Rate(Reading<double>.Ready(double.PositiveInfinity)));
        Equal("—", Formatting.LinkSpeed(Reading<ulong>.Ready(0)));
        Equal("지원 안 됨", Formatting.StateLabel(ReadingState.Unsupported));
        Equal("접근 거부", Formatting.StateLabel(ReadingState.AccessDenied));
    }),
    ("Unknown enum names recover defaults without discarding other preferences", () => WithTemp(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, """{"Theme":"Sepia","WidgetMode":"Huge","BackgroundTransparency":61,"AlwaysOnTop":false}""");
        var loaded = new SettingsStore(path).Load();
        Equal(ThemeMode.Dark, loaded.Theme);
        Equal(WidgetMode.Compact, loaded.WidgetMode);
        Equal(61, loaded.BackgroundTransparency);
        Check(!loaded.AlwaysOnTop, "Valid preferences survive unknown enum names");
        File.WriteAllText(path, """{"Theme":"light","WidgetMode":"EXPANDED"}""");
        loaded = new SettingsStore(path).Load();
        Equal(ThemeMode.Light, loaded.Theme);
        Equal(WidgetMode.Expanded, loaded.WidgetMode);
        Equal(0, Directory.GetFiles(directory, "settings.json.corrupt-*.json").Length);
    })),
    ("Unknown enum dictionary keys drop only that entry", () => WithTemp(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, """{"AlwaysOnTop":false,"Placements":{"Huge":{"Left":5,"Top":6},"Expanded":{"Left":7,"Top":8}},"DisplayProfiles":{"Compact":{"Items":{"Disk":{"Visible":false,"Elements":["usage"]},"Cpu":{"Visible":false,"Elements":["graph"]}}}}}""");
        var loaded = new SettingsStore(path).Load();
        Check(!loaded.AlwaysOnTop, "Unrelated preference retained");
        Equal(1, loaded.Placements.Count);
        Near(7, loaded.Placements[WidgetMode.Expanded].Left);
        Equal(4, loaded.DisplayProfiles.Compact.Items.Count);
        Check(!loaded.DisplayProfiles.Compact.Items[MetricKind.Cpu].Visible, "Known metric key retained");
        Equal(0, Directory.GetFiles(directory, "settings.json.corrupt-*.json").Length);
    })),
    ("Empty, null and array settings files recover defaults with a backup", () => WithTemp(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        foreach (var content in new[] { "", "null", "[]" })
        {
            foreach (var old in Directory.GetFiles(directory, "settings.json.corrupt-*.json")) File.Delete(old);
            File.WriteAllText(path, content);
            var loaded = new SettingsStore(path).Load();
            Check(loaded.AlwaysOnTop && loaded.Theme == ThemeMode.Dark, $"Defaults for '{content}'");
            var backups = Directory.GetFiles(directory, "settings.json.corrupt-*.json");
            Equal(1, backups.Length);
            Equal(content, File.ReadAllText(backups[0]));
        }
    })),
    ("A locked settings file is preserved by the next save", () => WithTemp(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        const string original = """{"AlwaysOnTop":false,"BackgroundTransparency":61}""";
        File.WriteAllText(path, original);
        var store = new SettingsStore(path);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var loaded = store.Load();
            Check(loaded.AlwaysOnTop, "Unreadable file yields defaults");
            Check(store.LastLoadFailed, "Unreadable load is remembered");
        }
        store.Save(new AppSettings { BackgroundTransparency = 10 });
        Check(!store.LastLoadFailed, "Successful save clears the failure");
        var preserved = Directory.GetFiles(directory, "settings.json.unreadable-*.json");
        Equal(1, preserved.Length);
        Equal(original, File.ReadAllText(preserved[0]));
        Equal(10, new SettingsStore(path).Load().BackgroundTransparency);
        store.Save(new AppSettings());
        Equal(1, Directory.GetFiles(directory, "settings.json.unreadable-*.json").Length);
        Equal(0, Directory.GetFiles(directory, "*.tmp").Length);
    })),
    ("Formatting promotes values that round up to the next unit", () =>
    {
        Equal("1 GiB", Formatting.Bytes(Reading<ulong>.Ready((ulong)(1023.95 * 1024 * 1024))));
        Equal("1023 MiB", Formatting.Bytes(Reading<ulong>.Ready((ulong)(1023.4 * 1024 * 1024))));
        Equal("1 MB/s", Formatting.Rate(Reading<double>.Ready(999_999)));
        Equal("1 MB/s", Formatting.Rate(Reading<double>.Ready(999_949)));
        Equal("999 KB/s", Formatting.Rate(Reading<double>.Ready(999_499)));
        Equal("1 KB/s", Formatting.Rate(Reading<double>.Ready(999.6)));
        Equal("1023 B", Formatting.Bytes(Reading<ulong>.Ready(1023)));
        Equal("100%", Formatting.Percent(Reading<double>.Ready(99.5)));
        Equal("99%", Formatting.Percent(Reading<double>.Ready(99.49)));
    }),
    ("History marks a gap only when samples are more than 2.5 seconds apart", () =>
    {
        var start = DateTimeOffset.UtcNow;
        var exact = new HistoryBuffer();
        exact.Add(Snapshot(start));
        exact.Add(Snapshot(start + TimeSpan.FromMilliseconds(2500)));
        Equal(2, exact.Samples.Count);
        Check(exact.Samples.All(x => x.Cpu is not null), "Exactly 2.5 seconds is not a pause");
        var late = new HistoryBuffer();
        late.Add(Snapshot(start));
        late.Add(Snapshot(start + TimeSpan.FromMilliseconds(2600)));
        Equal(3, late.Samples.Count);
        Check(late.Samples[1].Cpu is null && late.Samples[1].Download is null, "2.6 seconds inserts one gap");
    })
};

var failures = 0;
foreach (var (name, run) in cases)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {name}\n{exception}"); }
}
Console.WriteLine($"{cases.Length - failures}/{cases.Length} checks passed.");
return failures == 0 ? 0 : 1;

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
}
static void Near(double expected, double actual)
{
    if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 0.00001)
        throw new InvalidOperationException($"Expected {expected}, received {actual}.");
}
static void WithTemp(Action<string> test)
{
    var directory = Path.Combine(Path.GetTempPath(), "ResourceStatus.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try { test(directory); }
    finally { Directory.Delete(directory, recursive: true); }
}
static SystemSnapshot Snapshot(DateTimeOffset timestamp, double cpu = 25) => new(timestamp,
    new CpuSnapshot("CPU", Reading<double>.Ready(cpu)),
    new RamSnapshot(Reading<double>.Ready(50), Reading<ulong>.Ready(16UL << 30), Reading<ulong>.Ready(32UL << 30)),
    new GpuSnapshot("gpu1", "GPU", Reading<double>.Ready(12), Reading<ulong>.Ready(2UL << 30), Reading<ulong>.Ready(8UL << 30), Reading<ulong>.Ready(0)),
    new NetworkSnapshot("net1", "Ethernet", Reading<double>.Ready(1000), Reading<double>.Ready(200), Reading<double>.Ready(1),
        Reading<ulong>.Ready(1_000_000_000), Reading<ulong>.Ready(1_000_000_000), "인터넷 연결"));

static void RejectUpdate(Action run)
{
    try { run(); }
    catch (ReleaseUpdateException) { return; }
    throw new InvalidOperationException("Invalid update was not rejected");
}

static ReleaseVersion Semantic(string version)
{
    if (!ReleaseVersion.TryParse(version, out var parsed)) throw new InvalidOperationException("Invalid fixture version: " + version);
    return parsed;
}

static (ReleaseUpdate Update, byte[] Installer, byte[] Manifest, string Json) UpdateFixture(string version = "0.0.2", bool? prerelease = null)
{
    byte[] installer = [1, 2, 3, 4];
    string digest = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant();
    string installerName = $"ResourceStatus-{version}-win-x64-setup.exe";
    byte[] manifest = Encoding.UTF8.GetBytes($"{digest}  {installerName}\n");
    object Asset(string name, byte[] bytes) => new
    {
        name, state = "uploaded", size = bytes.Length,
        browser_download_url = $"{ReleaseUpdateMetadata.RepositoryUrl}/releases/download/v{version}/{name}",
        digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
    };
    string json = JsonSerializer.Serialize(new { tag_name = $"v{version}", draft = false, prerelease = prerelease ?? Semantic(version).IsPrerelease,
        assets = new[] { Asset(installerName, installer), Asset("SHA256SUMS.txt", manifest) } });
    return (ReleaseUpdateMetadata.Parse(json, new Version(0, 0, 0), includePrereleases: true)!, installer, manifest, json);
}

static FakeUpdateHandler UpdateDownloadHandler((ReleaseUpdate Update, byte[] Installer, byte[] Manifest, string Json) fixture, byte[]? installer = null) =>
    new((request, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.RequestUri == fixture.Update.ChecksumsUrl)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Manifest) });
        if (request.RequestUri == fixture.Update.InstallerUrl)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer ?? fixture.Installer) });
        throw new InvalidOperationException("Unexpected request: " + request.RequestUri);
    });

sealed class FakeUpdateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}

sealed class ImmediateProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
