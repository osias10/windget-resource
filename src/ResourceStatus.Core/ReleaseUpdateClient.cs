using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace ResourceStatus.Core;

/// <summary>Downloads are never executed here. All bytes must pass size and SHA-256 verification before a final .exe is exposed.</summary>
public sealed class ReleaseUpdateClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private DateTimeOffset _nextCheck;
    private Version? _checkedVersion;
    private ReleaseUpdate? _cachedUpdate;
    private string? _cachedError;
    private readonly ConcurrentDictionary<string, (long Size, string Checksum)> _verifiedDownloads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ReleaseUpdateClient() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }

    // The injectable handler permits deterministic offline tests. Production redirects are always handled and checked here.
    public ReleaseUpdateClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ResourceStatus-Updater/1.0");
    }

    public async Task<ReleaseUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        await _checkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var version = ReleaseUpdateMetadata.NormalizeVersion(currentVersion);
            if (_checkedVersion == version && DateTimeOffset.UtcNow < _nextCheck)
            {
                if (_cachedError is not null) throw new ReleaseUpdateException(_cachedError);
                return _cachedUpdate;
            }
            _checkedVersion = version;
            _cachedUpdate = null;
            _cachedError = null;
            _nextCheck = DateTimeOffset.UtcNow.AddSeconds(30);
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseUpdateMetadata.LatestReleaseUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            EnsureSuccess(response);
            byte[] bytes = await ReadBoundedAsync(response, ReleaseUpdateMetadata.MaximumMetadataSize, null, timeout.Token).ConfigureAwait(false);
            _cachedUpdate = ReleaseUpdateMetadata.Parse(StrictUtf8.GetString(bytes), version);
            return _cachedUpdate;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _cachedError = "업데이트 확인 시간이 초과되었습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요.";
            throw new ReleaseUpdateException(_cachedError, exception);
        }
        catch (OperationCanceledException)
        {
            _nextCheck = DateTimeOffset.MinValue;
            throw;
        }
        catch (ReleaseUpdateException exception)
        {
            _cachedError = exception.Message;
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or DecoderFallbackException)
        {
            _cachedError = "GitHub에 연결할 수 없습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요.";
            throw new ReleaseUpdateException(_cachedError, exception);
        }
        finally { _checkLock.Release(); }
    }

    public async Task<string> DownloadAsync(ReleaseUpdate update, string directory, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        string? attemptDirectory = null;
        string? partial = null;
        bool complete = false;
        try
        {
            progress?.Report(0);
            using var checksumResponse = await GetAssetAsync(update.ChecksumsUrl, timeout.Token).ConfigureAwait(false);
            byte[] manifestBytes = await ReadBoundedAsync(checksumResponse, ReleaseUpdateMetadata.MaximumChecksumsSize,
                update.ChecksumsSize, timeout.Token).ConfigureAwait(false);
            if (update.ChecksumsDigest is not null && !ReleaseUpdateMetadata.HashMatches(Convert.ToHexString(SHA256.HashData(manifestBytes)), update.ChecksumsDigest))
                throw ReleaseUpdateMetadata.InvalidChecksum();
            string checksum = ReleaseUpdateMetadata.ReadInstallerChecksum(StrictUtf8.GetString(manifestBytes), update.InstallerFileName, update.InstallerDigest);
            using var installerResponse = await GetAssetAsync(update.InstallerUrl, timeout.Token).ConfigureAwait(false);
            if (installerResponse.Content.Headers.ContentLength is { } contentLength && contentLength != update.InstallerSize)
                throw ReleaseUpdateMetadata.InvalidChecksum();

            // A unique child directory prevents a previous/cancelled download from being mistaken for the verified file.
            attemptDirectory = Path.Combine(Path.GetFullPath(directory), $"{update.VersionText}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(attemptDirectory);
            partial = Path.Combine(attemptDirectory, update.InstallerFileName + ".partial");
            string finalPath = Path.Combine(attemptDirectory, update.InstallerFileName);
            long length = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var input = await installerResponse.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    length += read;
                    if (length > update.InstallerSize || length > ReleaseUpdateMetadata.MaximumInstallerSize)
                        throw ReleaseUpdateMetadata.InvalidChecksum();
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    progress?.Report(Math.Min(99, length * 100d / update.InstallerSize));
                }
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            if (length != update.InstallerSize || !ReleaseUpdateMetadata.HashMatches(Convert.ToHexString(hash.GetHashAndReset()), checksum))
                throw ReleaseUpdateMetadata.InvalidChecksum();
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partial, finalPath);
            _verifiedDownloads[finalPath] = (length, checksum);
            complete = true;
            progress?.Report(100);
            return finalPath;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseUpdateException("다운로드 시간이 초과되었습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or DecoderFallbackException)
        {
            throw new ReleaseUpdateException("설치 파일을 다운로드할 수 없습니다. 인터넷 연결과 저장 공간을 확인한 뒤 다시 시도해 주세요.", exception);
        }
        finally
        {
            if (!complete && attemptDirectory is not null)
            {
                // Delete only this operation's exact temporary file and now-empty directory, never recursively.
                try { if (partial is not null) File.Delete(partial); Directory.Delete(attemptDirectory); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public async Task VerifyDownloadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_verifiedDownloads.TryGetValue(Path.GetFullPath(path), out var expected)) throw ReleaseUpdateMetadata.InvalidChecksum();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length != expected.Size || !ReleaseUpdateMetadata.HashMatches(Convert.ToHexString(await SHA256.HashDataAsync(input, timeout.Token).ConfigureAwait(false)), expected.Checksum))
                throw ReleaseUpdateMetadata.InvalidChecksum();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseUpdateException("설치 파일 검증 시간이 초과되었습니다. 다시 시도해 주세요.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ReleaseUpdateException("다운로드한 설치 파일을 읽을 수 없습니다. 다시 다운로드해 주세요.", exception);
        }
    }

    private async Task<HttpResponseMessage> GetAssetAsync(Uri original, CancellationToken cancellationToken)
    {
        Uri current = original;
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw ReleaseUpdateMetadata.InvalidMetadata();
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!ReleaseUpdateMetadata.IsTrustedAssetRedirect(current, original)) throw ReleaseUpdateMetadata.InvalidMetadata();
                continue;
            }
            try { EnsureSuccess(response); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new ReleaseUpdateException("설치 파일의 다운로드 주소를 확인할 수 없습니다. 잠시 후 다시 시도해 주세요.");
    }

    private void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.OK) return;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out long resetSeconds))
            {
                try { retry = DateTimeOffset.FromUnixTimeSeconds(resetSeconds) - DateTimeOffset.UtcNow; }
                catch (ArgumentOutOfRangeException) { }
            }
            _nextCheck = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(retry?.TotalSeconds ?? 60, 30, 3600));
            throw new ReleaseUpdateException("GitHub 요청이 잠시 제한되었습니다. 잠시 후 다시 확인해 주세요.");
        }
        throw new ReleaseUpdateException($"GitHub에서 파일을 가져오지 못했습니다 (HTTP {(int)response.StatusCode}). 잠시 후 다시 시도해 주세요.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, long? expectedSize, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } contentLength && (contentLength > maximum || (expectedSize is not null && contentLength != expectedSize)))
            throw ReleaseUpdateMetadata.InvalidMetadata();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > maximum) throw ReleaseUpdateMetadata.InvalidMetadata();
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (expectedSize is not null && output.Length != expectedSize) throw ReleaseUpdateMetadata.InvalidMetadata();
        return output.ToArray();
    }

    public void Dispose()
    {
        _http.Dispose();
        // No WaitHandle is ever requested from this managed semaphore. Let the GC reclaim it;
        // disposing it here would race an in-flight CheckAsync's finally/Release during app shutdown.
    }
}
