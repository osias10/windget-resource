using ResourceStatus.Core;
using ResourceStatus.Native;
using System.Text.Json;
using System.Net;
using System.Security.Cryptography;
using System.Text;

var cases = new (string Name, Action Run)[]
{
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

static (ReleaseUpdate Update, byte[] Installer, byte[] Manifest, string Json) UpdateFixture(string version = "0.0.2")
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
    string json = JsonSerializer.Serialize(new { tag_name = $"v{version}", draft = false, prerelease = false,
        assets = new[] { Asset(installerName, installer), Asset("SHA256SUMS.txt", manifest) } });
    return (ReleaseUpdateMetadata.Parse(json, new Version(0, 0, 0))!, installer, manifest, json);
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
