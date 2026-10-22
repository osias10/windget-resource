using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ResourceStatus.Core;

namespace ResourceStatus.App;

public interface IUpdateService
{
    Task<ReleaseUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default);
    Task<ReleaseUpdate?> CheckAsync(ReleaseVersion currentVersion, bool includePrereleases, CancellationToken cancellationToken = default);
    Task<string> DownloadAsync(ReleaseUpdate update, string directory, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task VerifyDownloadAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class UpdateService : IUpdateService, IDisposable
{
    private readonly ReleaseUpdateClient _client = new();
    // AssemblyVersion has a fourth revision component; releases intentionally compare only major.minor.patch.
    public static Version CurrentVersion => ReleaseUpdateMetadata.NormalizeVersion(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));
    public static ReleaseVersion CurrentReleaseVersion => ReleaseVersion.TryParse(
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, out var version)
        ? version : ReleaseVersion.FromVersion(CurrentVersion);
    public static string CurrentVersionText => CurrentReleaseVersion.PrecedenceText;
    public Task<ReleaseUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default) => _client.CheckAsync(currentVersion, cancellationToken);
    public Task<ReleaseUpdate?> CheckAsync(Version currentVersion, bool includePrereleases, CancellationToken cancellationToken = default) => _client.CheckAsync(currentVersion, includePrereleases, cancellationToken);
    public Task<ReleaseUpdate?> CheckAsync(ReleaseVersion currentVersion, bool includePrereleases, CancellationToken cancellationToken = default) => _client.CheckAsync(currentVersion, includePrereleases, cancellationToken);
    public Task<string> DownloadAsync(ReleaseUpdate update, string directory, IProgress<double>? progress = null, CancellationToken cancellationToken = default) => _client.DownloadAsync(update, directory, progress, cancellationToken);
    public Task VerifyDownloadAsync(string path, CancellationToken cancellationToken = default) => _client.VerifyDownloadAsync(path, cancellationToken);
    public void Dispose() => _client.Dispose();
}
