using System.Security.Cryptography;
using System.Text.Json;

namespace ResourceStatus.Core;

public sealed class ReleaseUpdate
{
    internal ReleaseUpdate(ReleaseVersion version, bool prerelease, Uri installerUrl, long installerSize, string? installerDigest,
        Uri checksumsUrl, long checksumsSize, string? checksumsDigest)
    {
        SemanticVersion = version;
        IsPrerelease = prerelease || version.IsPrerelease;
        InstallerUrl = installerUrl;
        InstallerSize = installerSize;
        InstallerDigest = installerDigest;
        ChecksumsUrl = checksumsUrl;
        ChecksumsSize = checksumsSize;
        ChecksumsDigest = checksumsDigest;
    }

    public Version Version => SemanticVersion.NumericVersion;
    public ReleaseVersion SemanticVersion { get; }
    public bool IsPrerelease { get; }
    public string VersionText => SemanticVersion.Text;
    public Uri ReleaseUrl => new($"{ReleaseUpdateMetadata.RepositoryUrl}/releases/tag/v{VersionText}");
    public string InstallerFileName => $"ResourceStatus-{VersionText}-win-x64-setup.exe";
    public Uri InstallerUrl { get; }
    public long InstallerSize { get; }
    public string? InstallerDigest { get; }
    public Uri ChecksumsUrl { get; }
    public long ChecksumsSize { get; }
    public string? ChecksumsDigest { get; }
}

public sealed class ReleaseUpdateException : Exception
{
    public ReleaseUpdateException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>Published releases and exact assets from this application's repository; prereleases require explicit opt-in.</summary>
public static class ReleaseUpdateMetadata
{
    public const string RepositoryUrl = "https://github.com/osias10/windget-resource";
    public const string LatestReleaseUrl = "https://api.github.com/repos/osias10/windget-resource/releases/latest";
    public const string ReleasesUrl = "https://api.github.com/repos/osias10/windget-resource/releases";
    public const long MaximumInstallerSize = 512L * 1024 * 1024;
    public const int MaximumMetadataSize = 1024 * 1024;
    public const int MaximumChecksumsSize = 64 * 1024;
    public const int MaximumReleasePages = 10;

    public static Version NormalizeVersion(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (!TryParseReleaseTag(tag, out var parsed) || parsed.IsPrerelease || parsed.BuildMetadata is not null) return false;
        version = parsed.NumericVersion;
        return true;
    }

    public static bool TryParseReleaseTag(string? tag, out ReleaseVersion version) =>
        ReleaseVersion.TryParse(tag is not null && tag.StartsWith('v') ? tag[1..] : null, out version);

    public static bool IsInstallerFileName(string name)
    {
        const string prefix = "ResourceStatus-", suffix = "-win-x64-setup.exe";
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal) &&
            name.Length > prefix.Length + suffix.Length && ReleaseVersion.TryParse(name[prefix.Length..^suffix.Length], out _);
    }

    public static ReleaseUpdate? Parse(string json, Version currentVersion, bool includePrereleases = false) =>
        Parse(json, ReleaseVersion.FromVersion(currentVersion), includePrereleases);

    public static ReleaseUpdate? Parse(string json, ReleaseVersion currentVersion, bool includePrereleases = false)
    {
        if (json.Length > MaximumMetadataSize) throw InvalidMetadata();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var candidate = ReadCandidate(document.RootElement, currentVersion, includePrereleases);
            return candidate is null ? null : CreateUpdate(candidate);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw InvalidMetadata(exception);
        }
    }

    public static ReleaseUpdate? ParseList(string json, ReleaseVersion currentVersion, bool includePrereleases = false)
    {
        var candidate = SelectListCandidate(json, currentVersion, includePrereleases);
        return candidate is null ? null : CreateUpdate(candidate);
    }

    internal sealed record Candidate(ReleaseVersion Version, bool Prerelease, JsonElement Metadata);

    internal static Candidate? SelectListCandidate(string json, ReleaseVersion currentVersion, bool includePrereleases)
    {
        if (json.Length > MaximumMetadataSize) throw InvalidMetadata();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.GetArrayLength() > 100) throw InvalidMetadata();
            Candidate? selected = null;
            foreach (var release in document.RootElement.EnumerateArray())
            {
                var candidate = ReadCandidate(release, currentVersion, includePrereleases);
                if (candidate is not null && IsBetterCandidate(candidate, selected)) selected = candidate;
            }
            return selected;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw InvalidMetadata(exception); }
    }

    private static Candidate? ReadCandidate(JsonElement root, ReleaseVersion currentVersion, bool includePrereleases)
    {
        if (root.GetProperty("draft").GetBoolean()) return null;
        bool prerelease = root.GetProperty("prerelease").GetBoolean();
        if (!TryParseReleaseTag(root.GetProperty("tag_name").GetString(), out var version) ||
            (!includePrereleases && (prerelease || version.IsPrerelease)) || version.CompareTo(currentVersion) <= 0) return null;
        return new Candidate(version, prerelease, root.Clone());
    }

    internal static bool IsBetterCandidate(Candidate candidate, Candidate? selected) => selected is null ||
        candidate.Version.CompareTo(selected.Version) > 0 ||
        (candidate.Version.Equals(selected.Version) && ((!candidate.Prerelease && selected.Prerelease) ||
            (candidate.Prerelease == selected.Prerelease && string.CompareOrdinal(candidate.Version.Text, selected.Version.Text) > 0)));

    internal static ReleaseUpdate CreateUpdate(Candidate candidate)
    {
        try
        {
            string versionText = candidate.Version.Text;
            string installerName = $"ResourceStatus-{versionText}-win-x64-setup.exe";
            var assets = candidate.Metadata.GetProperty("assets");
            var installer = FindAsset(assets, versionText, installerName, MaximumInstallerSize);
            var checksums = FindAsset(assets, versionText, "SHA256SUMS.txt", MaximumChecksumsSize);
            return new ReleaseUpdate(candidate.Version, candidate.Prerelease, installer.Url, installer.Size, installer.Digest,
                checksums.Url, checksums.Size, checksums.Digest);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw InvalidMetadata(exception); }
    }

    private static (Uri Url, long Size, string? Digest) FindAsset(JsonElement assets, string version, string name, long maximumSize)
    {
        JsonElement? found = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            if (found is not null) throw InvalidMetadata();
            found = asset;
        }
        if (found is not { } value || value.GetProperty("state").GetString() != "uploaded")
            throw new ReleaseUpdateException("새 버전의 설치 파일 또는 체크섬이 아직 준비되지 않았습니다. 잠시 후 다시 확인해 주세요.");
        string? url = value.GetProperty("browser_download_url").GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsOfficialAssetUrl(uri, version, name)) throw InvalidMetadata();
        long size = value.GetProperty("size").GetInt64();
        if (size <= 0 || size > maximumSize) throw InvalidMetadata();
        string? digest = null;
        if (value.TryGetProperty("digest", out var digestValue) && digestValue.ValueKind != JsonValueKind.Null)
        {
            string? text = digestValue.GetString();
            if (text is null || !text.StartsWith("sha256:", StringComparison.Ordinal) || !IsSha256(text[7..])) throw InvalidMetadata();
            digest = text[7..].ToLowerInvariant();
        }
        return (uri, size, digest);
    }

    public static bool IsOfficialAssetUrl(Uri uri, string version, string fileName) =>
        IsSecureUri(uri) && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        Uri.UnescapeDataString(uri.AbsolutePath) == $"/osias10/windget-resource/releases/download/v{version}/{fileName}" &&
        string.IsNullOrEmpty(uri.Query);

    public static bool IsTrustedAssetRedirect(Uri uri, Uri original) => IsSecureUri(uri) &&
        ((uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == original.AbsolutePath && string.IsNullOrEmpty(uri.Query)) ||
         uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsSecureUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

    public static string ReadInstallerChecksum(string manifest, string installerName, string? metadataDigest)
    {
        if (manifest.Length > MaximumChecksumsSize) throw InvalidChecksum();
        string? selected = null;
        foreach (string raw in manifest.TrimStart('\uFEFF').Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length < 67 || !IsSha256(line[..64]) || line[64] != ' ' || line[65] is not (' ' or '*')) throw InvalidChecksum();
            string name = line[66..];
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', '\0']) >= 0) throw InvalidChecksum();
            if (name != installerName) continue;
            if (selected is not null) throw InvalidChecksum();
            selected = line[..64].ToLowerInvariant();
        }
        if (selected is null || (metadataDigest is not null && !HashMatches(selected, metadataDigest))) throw InvalidChecksum();
        return selected;
    }

    public static bool HashMatches(string actual, string expected) => IsSha256(actual) && IsSha256(expected) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected));

    public static bool IsSha256(string text) => text.Length == 64 && text.All(char.IsAsciiHexDigit);

    internal static ReleaseUpdateException InvalidMetadata(Exception? inner = null) => new("GitHub 업데이트 정보가 올바르지 않아 설치를 진행할 수 없습니다.", inner);
    internal static ReleaseUpdateException InvalidChecksum() => new("설치 파일의 SHA-256 검증에 실패했습니다. 다시 다운로드해 주세요.");
}
