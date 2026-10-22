using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using ResourceStatus.Core;

namespace ResourceStatus.App;

internal static class UpdateInstaller
{
    internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{38E03914-6824-47B5-AB1A-BDB03661E1A2}_is1";

    public static void Start(string installerPath, string downloadDirectory)
    {
        string path = Path.GetFullPath(installerPath);
        string root = Path.GetFullPath(downloadDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !ReleaseUpdateMetadata.IsInstallerFileName(Path.GetFileName(path)))
            throw new InvalidOperationException("설치 파일 경로가 올바르지 않습니다.");
        if (!File.Exists(path)) throw new FileNotFoundException("업데이트 설치 파일을 찾을 수 없습니다.", path);

        using var process = Process.Start(CreateStartInfo(path, Environment.ProcessId, IsInstalledCopy()));
        if (process is null) throw new InvalidOperationException("설치 프로그램을 시작하지 못했습니다.");
    }

    internal static ProcessStartInfo CreateStartInfo(string path, int processId, bool installed)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        info.ArgumentList.Add("/SP-");
        info.ArgumentList.Add("/NORESTART");
        info.ArgumentList.Add("/UPDATEPID=" + processId);
        info.ArgumentList.Add("/RESTARTWIDGET=1");
        if (installed) info.ArgumentList.Add("/SILENT");
        return info;
    }

    private static bool IsInstalledCopy()
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = registry.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is not string installedPath || string.IsNullOrWhiteSpace(installedPath)) return false;
            return string.Equals(Path.GetFullPath(installedPath).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { return false; }
    }

}
