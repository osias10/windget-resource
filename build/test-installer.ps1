[CmdletBinding()]
param([string]$InstallerPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $workspaceRoot '.artifacts'
$version = [string]([xml](Get-Content -LiteralPath (Join-Path $workspaceRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (-not $InstallerPath) { $InstallerPath = Join-Path $artifactRoot "ResourceStatus-$version-win-x64-setup.exe" }
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{38E03914-6824-47B5-AB1A-BDB03661E1A2}_is1'
if (Test-Path -LiteralPath $uninstallKey) {
    throw 'ResourceStatus is already installed for this user. Run installer validation in a clean Windows user account to preserve the existing installation.'
}

$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$testRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot "installer-validation\$runId"))
$installDir = Join-Path $testRoot 'app'
$artifactPrefix = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\') + '\'
if (-not $installDir.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer validation must stay inside .artifacts.'
}
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$userSettings = Join-Path $env:LOCALAPPDATA 'ResourceStatus\settings.json'
$settingsHashBefore = if (Test-Path -LiteralPath $userSettings) { (Get-FileHash -LiteralPath $userSettings -Algorithm SHA256).Hash } else { $null }
$uninstaller = Join-Path $installDir 'unins000.exe'
$shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'ResourceStatus\ResourceStatus.lnk'),
    (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'ResourceStatus.lnk')
)
$shortcutHashes = @{}
foreach ($shortcut in $shortcutPaths) {
    $shortcutHashes[$shortcut] = if (Test-Path -LiteralPath $shortcut) { (Get-FileHash -LiteralPath $shortcut -Algorithm SHA256).Hash } else { $null }
}

function Invoke-CheckedProcess {
    param([string]$FilePath, [string[]]$ProcessArguments)
    $process = Start-Process -FilePath $FilePath -ArgumentList $ProcessArguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(180000)) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
        throw "Validation process timed out: $FilePath"
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw "Validation process failed ($($process.ExitCode)): $FilePath" }
}

$removed = $false
try {
    Invoke-CheckedProcess -FilePath $InstallerPath -ProcessArguments @('/VERYSILENT', '/SUPPRESSMSGBOXES',
        '/NORESTART', '/NOICONS', '/TASKS=', ('/DIR="' + $installDir + '"'), ('/LOG="' + (Join-Path $testRoot 'install.log') + '"'))
    foreach ($shortcut in $shortcutPaths) {
        $installedShortcutHash = if (Test-Path -LiteralPath $shortcut) { (Get-FileHash -LiteralPath $shortcut -Algorithm SHA256).Hash } else { $null }
        if ($shortcutHashes[$shortcut] -ne $installedShortcutHash) { throw 'Silent validation must not create or modify Start Menu or desktop shortcuts.' }
    }
    $registration = Get-ItemProperty -LiteralPath $uninstallKey
    if ($registration.DisplayVersion -ne $version) { throw 'Installed application version is incorrect.' }
    if ($registration.InstallLocation.TrimEnd('\') -ne $installDir.TrimEnd('\')) { throw 'Installer did not use the isolated validation directory.' }
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw 'Uninstaller was not installed.' }

    $publishDir = Join-Path $artifactRoot 'publish'
    $payload = @(Get-ChildItem -LiteralPath $publishDir -File -Recurse)
    if ($payload.Count -eq 0) { throw 'Published payload is empty.' }
    foreach ($file in $payload) {
        $relativePath = $file.FullName.Substring($publishDir.Length).TrimStart('\')
        $installedFile = Join-Path $installDir $relativePath
        if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf)) { throw "Missing installed file: $relativePath" }
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash) { throw "Installed payload differs: $relativePath" }
    }

    # Exercise an actual update while the old executable is locked and its
    # single-instance mutex is held. Setup must wait for that process to exit.
    $parentScript = Join-Path $testRoot 'update-parent.ps1'
    $parentReady = Join-Path $testRoot 'parent-ready.txt'
    @'
param([string]$Executable, [string]$ReadyFile)
$ErrorActionPreference = 'Stop'
$mutex = [Threading.Mutex]::new($true, 'Local\ResourceStatus.Widget')
$stream = [IO.File]::Open($Executable, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    [IO.File]::WriteAllText($ReadyFile, 'ready')
    Start-Sleep -Seconds 8
}
finally { $stream.Dispose(); $mutex.ReleaseMutex(); $mutex.Dispose() }
'@ | Set-Content -LiteralPath $parentScript -Encoding UTF8
    $parentArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $parentScript + '"'),
        '-Executable', ('"' + (Join-Path $installDir 'ResourceStatus.exe') + '"'), '-ReadyFile', ('"' + $parentReady + '"'))
    $parentShell = if (Test-Path -LiteralPath (Join-Path $PSHOME 'pwsh.exe')) { Join-Path $PSHOME 'pwsh.exe' } else { Join-Path $PSHOME 'powershell.exe' }
    $parent = Start-Process -FilePath $parentShell -ArgumentList $parentArguments -WindowStyle Hidden -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        while (-not (Test-Path -LiteralPath $parentReady)) {
            if ($parent.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Update parent fixture did not become ready.' }
            Start-Sleep -Milliseconds 100
        }
        $updateLog = Join-Path $testRoot 'update.log'
        Invoke-CheckedProcess -FilePath $InstallerPath -ProcessArguments @('/VERYSILENT', '/SUPPRESSMSGBOXES',
            '/NORESTART', '/NOICONS', '/TASKS=', ('/UPDATEPID=' + $parent.Id), ('/DIR="' + $installDir + '"'), ('/LOG="' + $updateLog + '"'))
        if (-not $parent.HasExited) { throw 'Setup did not wait for the old application to exit.' }
        if (-not (Select-String -LiteralPath $updateLog -SimpleMatch 'Waiting for ResourceStatus update parent' -Quiet)) {
            throw 'The setup update handoff was not exercised.'
        }
    }
    finally {
        if (-not $parent.HasExited) { Stop-Process -Id $parent.Id }
        $parent.Dispose()
    }

    $smokeOutput = Join-Path $testRoot 'smoke.json'
    Invoke-CheckedProcess -FilePath (Join-Path $installDir 'ResourceStatus.exe') -ProcessArguments @('--smoke-test',
        '--settings-dir', ('"' + (Join-Path $testRoot 'settings') + '"'), '--output', ('"' + $smokeOutput + '"'))
    if (-not (Test-Path -LiteralPath $smokeOutput -PathType Leaf)) { throw 'Installed application did not produce its smoke report.' }

    # Preserve unrelated files inside the selected directory; the uninstaller must
    # remove only its recorded payload, never recursively delete that directory.
    $sentinel = Join-Path $installDir 'user-owned-file.txt'
    Set-Content -LiteralPath $sentinel -Value 'Preserve this file.' -Encoding ascii
    Invoke-CheckedProcess -FilePath $uninstaller -ProcessArguments @('/VERYSILENT', '/SUPPRESSMSGBOXES',
        '/NORESTART', ('/LOG="' + (Join-Path $testRoot 'uninstall.log') + '"'))
    $removed = $true
    if (Test-Path -LiteralPath (Join-Path $installDir 'ResourceStatus.exe')) { throw 'Uninstall left the application executable.' }
    if (Test-Path -LiteralPath $uninstallKey) { throw 'Uninstall left the application registration.' }
    if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Uninstall removed an unrelated user file.' }
    $settingsHashAfter = if (Test-Path -LiteralPath $userSettings) { (Get-FileHash -LiteralPath $userSettings -Algorithm SHA256).Hash } else { $null }
    if ($settingsHashBefore -ne $settingsHashAfter) { throw 'Installer validation changed real user preferences.' }

    $reportPath = Join-Path $testRoot 'installer-validation.json'
    [ordered]@{
        success = $true
        version = $version
        verifiedPayloadFiles = $payload.Count
        installedAppSmokePassed = $true
        uninstallPassed = $true
        userPreferencesPreserved = $true
        unrelatedFilePreserved = $true
        shortcutsUnchanged = $true
        updateWaitsForPreviousProcess = $true
        smokeReport = $smokeOutput
    } | ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host "Installer validation passed: $reportPath"
}
finally {
    if (-not $removed -and (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        Invoke-CheckedProcess -FilePath $uninstaller -ProcessArguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    }
}
