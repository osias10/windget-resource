using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ResourceStatus.Core;

namespace ResourceStatus.App;

internal static class UpdateUiSmoke
{
    public static async Task RunAsync(List<string> checks, string directory)
    {
        var release = ReleaseUpdateMetadata.Parse("""
            {"draft":false,"prerelease":false,"tag_name":"v9.0.0","assets":[
              {"name":"ResourceStatus-9.0.0-win-x64-setup.exe","state":"uploaded","size":123,
               "browser_download_url":"https://github.com/osias10/windget-resource/releases/download/v9.0.0/ResourceStatus-9.0.0-win-x64-setup.exe"},
              {"name":"SHA256SUMS.txt","state":"uploaded","size":100,
               "browser_download_url":"https://github.com/osias10/windget-resource/releases/download/v9.0.0/SHA256SUMS.txt"}
            ]}
            """, new Version(0, 0, 0))!;
        var service = new FakeService();
        var window = new UpdateWindow(service, directory);
        string? handedOff = null;
        window.InstallRequested += path => handedOff = path;
        try
        {
            window.Show();
            Assert(service.CheckCount == 0, "Opening the update window does not contact the network automatically");

            var pendingCheck = new TaskCompletionSource<ReleaseUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Check = token => pendingCheck.Task.WaitAsync(token);
            Task check = window.CheckForUpdatesAsync();
            await window.CheckForUpdatesAsync();
            Assert(window.IsBusy && service.CheckCount == 1, "The update window prevents duplicate concurrent checks");
            window.CancelOperation();
            await check;
            Assert(!window.IsBusy && window.StatusText.Contains("취소", StringComparison.Ordinal), "Canceling a version check restores the update controls");

            service.Check = _ => Task.FromException<ReleaseUpdate?>(new HttpRequestException("private-debug-url.invalid"));
            await window.CheckForUpdatesAsync();
            Assert(!window.IsBusy && window.StatusText.Contains("인터넷", StringComparison.Ordinal)
                && !window.StatusText.Contains("private-debug", StringComparison.Ordinal), "Network errors are recoverable and show a user-facing message");
            service.Check = _ => Task.FromResult<ReleaseUpdate?>(null);
            await window.CheckForUpdatesAsync();
            Assert(!window.CanInstall && window.StatusText.Contains("최신 버전", StringComparison.Ordinal), "No-update responses cannot enable installation");

            service.Check = _ => Task.FromResult<ReleaseUpdate?>(release);
            await window.CheckForUpdatesAsync();
            Assert(window.ActionText == "다운로드" && !window.CanInstall, "A new release first offers download without starting installation");
            var pendingDownload = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Download = (_, token) => pendingDownload.Task.WaitAsync(token);
            Task download = window.DownloadOrInstallAsync();
            await window.DownloadOrInstallAsync();
            Assert(window.IsBusy && service.DownloadCount == 1, "The update window prevents duplicate concurrent downloads");
            window.CancelOperation();
            await download;
            Assert(!window.IsBusy && !window.CanInstall && handedOff is null, "A canceled download cannot trigger installer handoff");

            string installer = Path.Combine(directory, release.InstallerFileName);
            service.Download = (progress, _) => { progress?.Report(100); return Task.FromResult(installer); };
            await window.DownloadOrInstallAsync();
            Assert(window.CanInstall && window.ActionText == "설치하고 다시 시작" && handedOff is null,
                "Verified download completion waits for a separate explicit install click");
            service.Verify = _ => Task.FromException(new ReleaseUpdateException("설치 파일이 변경되었습니다. 다시 다운로드해 주세요."));
            await window.DownloadOrInstallAsync();
            Assert(!window.CanInstall && window.ActionText == "다운로드" && handedOff is null,
                "A changed installer is rejected and requires a new download");

            await window.DownloadOrInstallAsync();
            service.Verify = _ => Task.CompletedTask;
            await window.DownloadOrInstallAsync();
            Assert(service.VerifyCount == 2 && handedOff == installer && window.IsBusy,
                "Explicit installation verifies the file again before handing it to the application");
            window.ShowInstallError("설치 프로그램을 시작하지 못했습니다.");
            Assert(window.CanInstall && !window.IsBusy, "Installer startup errors restore the ready-to-install controls");
        }
        finally { window.Close(); }

        var closingService = new FakeService { Check = token => Task.Delay(Timeout.Infinite, token).ContinueWith<ReleaseUpdate?>(
            task => { task.GetAwaiter().GetResult(); return null; }, TaskScheduler.Default) };
        var closingWindow = new UpdateWindow(closingService, directory);
        closingWindow.Show();
        Task closingCheck = closingWindow.CheckForUpdatesAsync();
        closingWindow.Close();
        await closingCheck;
        Assert(!closingWindow.IsBusy && closingService.CheckCount == 1,
            "Closing the update window cancels its pending check without a late UI update");

        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("UI smoke assertion failed: " + message);
            checks.Add(message);
        }
    }

    private sealed class FakeService : IUpdateService
    {
        public Func<CancellationToken, Task<ReleaseUpdate?>> Check { get; set; } = _ => Task.FromResult<ReleaseUpdate?>(null);
        public Func<IProgress<double>?, CancellationToken, Task<string>> Download { get; set; } = (_, _) => Task.FromResult(string.Empty);
        public Func<CancellationToken, Task> Verify { get; set; } = _ => Task.CompletedTask;
        public int CheckCount { get; private set; }
        public int DownloadCount { get; private set; }
        public int VerifyCount { get; private set; }
        public Task<ReleaseUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
        { CheckCount++; return Check(cancellationToken); }
        public Task<string> DownloadAsync(ReleaseUpdate update, string directory, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        { DownloadCount++; return Download(progress, cancellationToken); }
        public Task VerifyDownloadAsync(string path, CancellationToken cancellationToken = default)
        { VerifyCount++; return Verify(cancellationToken); }
    }
}
