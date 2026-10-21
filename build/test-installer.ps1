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
        smokeReport = $smokeOutput
    } | ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host "Installer validation passed: $reportPath"
}
finally {
    if (-not $removed -and (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        Invoke-CheckedProcess -FilePath $uninstaller -ProcessArguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    }
}
