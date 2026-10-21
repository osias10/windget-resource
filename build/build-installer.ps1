[CmdletBinding()]
param(
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $workspaceRoot '.artifacts'
$publishPath = Join-Path $artifactRoot 'publish'

function Resolve-Iscc {
    if ($IsccPath) {
        $explicitPath = [IO.Path]::GetFullPath($IsccPath)
        if (-not (Test-Path -LiteralPath $explicitPath -PathType Leaf)) {
            throw "ISCC.exe was not found at -IsccPath: $explicitPath"
        }
        return $explicitPath
    }
    $localCompiler = Join-Path $workspaceRoot '.tools\innosetup\ISCC.exe'
    if (Test-Path -LiteralPath $localCompiler -PathType Leaf) { return $localCompiler }
    $installedCompiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($installedCompiler) { return $installedCompiler.Source }
    foreach ($programFilesRoot in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ($programFilesRoot) {
            $candidate = Join-Path $programFilesRoot 'Inno Setup 6\ISCC.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    throw 'Install Inno Setup 6, or supply -IsccPath C:\path\to\ISCC.exe.'
}

$version = [string]([xml](Get-Content -LiteralPath (Join-Path $workspaceRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Installer builds require a three-part numeric Version, found '$version'."
}
$expectedVersion = [Version]"$version.0"
foreach ($required in @('ResourceStatus.exe', 'ResourceStatus.dll', 'ResourceStatus.runtimeconfig.json',
    'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $required) -PathType Leaf)) {
        throw "Self-contained publish is missing $required. Run build/build.ps1 first."
    }
}
$executable = Join-Path $publishPath 'ResourceStatus.exe'
$fileInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
$actualVersion = [Version]::new($fileInfo.FileMajorPart, $fileInfo.FileMinorPart, $fileInfo.FileBuildPart, $fileInfo.FilePrivatePart)
if ($actualVersion -ne $expectedVersion) {
    throw "Published executable version $actualVersion does not match $expectedVersion. Run build/build.ps1 before packaging."
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishPath 'ResourceStatus.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.PSObject.Properties['framework'] -or
    $runtimeConfig.runtimeOptions.PSObject.Properties['frameworks']) {
    throw 'Published output requires a separately installed .NET runtime; use the self-contained build/build.ps1 output.'
}

$compiler = Resolve-Iscc
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stagingPath = [IO.Path]::GetFullPath((Join-Path $artifactRoot "installer\$runId"))
$outputName = "ResourceStatus-$version-win-x64-setup.exe"
$stagedInstaller = Join-Path $stagingPath $outputName
$installerPath = [IO.Path]::GetFullPath((Join-Path $artifactRoot $outputName))
$artifactPrefix = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\') + '\'
foreach ($target in @($stagingPath, $stagedInstaller, $installerPath)) {
    if (-not $target.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write an installer outside the artifact directory: $target"
    }
}
New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
$compilerArguments = @('/Qp', "/DAppVersion=$version", "/DPublishDir=$publishPath",
    "/DInstallerOutputDir=$stagingPath", (Join-Path $PSScriptRoot 'installer.iss'))
Write-Host "Inno Setup compiler: $compiler"
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath $stagedInstaller -PathType Leaf)) {
    throw "Inno Setup did not produce $stagedInstaller."
}
$setupInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($stagedInstaller)
$setupVersion = [Version]::new($setupInfo.FileMajorPart, $setupInfo.FileMinorPart, $setupInfo.FileBuildPart, $setupInfo.FilePrivatePart)
if ($setupVersion -ne $expectedVersion) {
    throw "Compiled installer version $setupVersion does not match $expectedVersion."
}
# Promote only a completed package; retain failed build staging for diagnosis.
Move-Item -LiteralPath $stagedInstaller -Destination $installerPath -Force
$sha256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$installerPath.sha256" -Value "$sha256  $outputName" -Encoding ascii
Write-Host "Installer: $installerPath"
Write-Host "SHA256: $sha256"
