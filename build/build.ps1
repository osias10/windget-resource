[CmdletBinding()]
param(
    [string]$DotnetPath,
    [switch]$SkipUiSmoke
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $workspaceRoot '.artifacts'

function Resolve-Dotnet {
    if ($DotnetPath) {
        $explicitPath = [IO.Path]::GetFullPath($DotnetPath)
        if (-not (Test-Path -LiteralPath $explicitPath -PathType Leaf)) {
            throw "dotnet.exe was not found at -DotnetPath: $explicitPath"
        }
        return $explicitPath
    }
    $localSdk = Join-Path $workspaceRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localSdk -PathType Leaf) { return $localSdk }
    $installedSdk = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($installedSdk) { return $installedSdk.Source }
    # Reuse the existing developer SDK without changing the reference project.
    $referenceSdk = [IO.Path]::GetFullPath((Join-Path $workspaceRoot '..\ai_sa\.tools\dotnet\dotnet.exe'))
    if (Test-Path -LiteralPath $referenceSdk -PathType Leaf) { return $referenceSdk }
    throw 'Install the .NET 10 SDK, or supply -DotnetPath C:\path\to\dotnet.exe.'
}

function Invoke-Dotnet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & $script:buildDotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

function Assert-ArtifactPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to move an output outside the artifact directory: $resolved"
    }
    return $resolved
}

$script:buildDotnet = Resolve-Dotnet
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stagingPath = Assert-ArtifactPath (Join-Path $artifactRoot "staging\$runId")
$publishPath = Assert-ArtifactPath (Join-Path $artifactRoot 'publish')
$validationPath = Assert-ArtifactPath (Join-Path $artifactRoot "validation\$runId")
$buildPath = Assert-ArtifactPath (Join-Path $artifactRoot "build\$runId")
$previousPath = Assert-ArtifactPath (Join-Path $artifactRoot "previous-publish-$runId")
$version = ([xml](Get-Content -LiteralPath (Join-Path $workspaceRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$packagePath = Assert-ArtifactPath (Join-Path $artifactRoot "ResourceStatus-$version-win-x64.zip")
$temporaryZip = Assert-ArtifactPath (Join-Path $artifactRoot "ResourceStatus-$version-win-x64-$runId.zip")

Push-Location -LiteralPath $workspaceRoot
try {
    Write-Host "SDK: $script:buildDotnet"
    Invoke-Dotnet -Arguments @('--version')
    Invoke-Dotnet -Arguments @('build', 'ResourceStatus.slnx', '-c', 'Release', '--artifacts-path', $buildPath, '--nologo')
    Invoke-Dotnet -Arguments @('run', '--project', 'tests/ResourceStatus.Tests/ResourceStatus.Tests.csproj', '-c', 'Release', '--artifacts-path', $buildPath, '--no-build')
    New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
    Invoke-Dotnet -Arguments @('publish', 'src/ResourceStatus.App/ResourceStatus.App.csproj', '-c', 'Release',
        '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
        '--artifacts-path', $buildPath, '-o', $stagingPath, '--nologo')

    $executable = Join-Path $stagingPath 'ResourceStatus.exe'
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $stagingPath 'ResourceStatus.runtimeconfig.json') -Raw | ConvertFrom-Json
    $frameworkReference = $runtimeConfig.runtimeOptions.PSObject.Properties['framework']
    $frameworkReferences = $runtimeConfig.runtimeOptions.PSObject.Properties['frameworks']
    if ($frameworkReference -or $frameworkReferences) { throw 'Published output still requires a separately installed runtime.' }
    foreach ($required in @('ResourceStatus.exe', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagingPath $required) -PathType Leaf)) {
            throw "Self-contained output is missing $required."
        }
    }
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'README.md') -Destination (Join-Path $stagingPath 'README.md')

    if (-not $SkipUiSmoke) {
        New-Item -ItemType Directory -Path $validationPath -Force | Out-Null
        $smokeSettings = Join-Path $validationPath 'settings'
        $smokeOutput = Join-Path $validationPath 'smoke.json'
        $smokeArguments = @('--smoke-test', '--settings-dir', ('"' + $smokeSettings + '"'), '--output', ('"' + $smokeOutput + '"'))
        $smokeProcess = Start-Process -FilePath $executable -ArgumentList $smokeArguments -WorkingDirectory $stagingPath -WindowStyle Hidden -PassThru
        if (-not $smokeProcess.WaitForExit(180000)) {
            Stop-Process -Id $smokeProcess.Id -ErrorAction SilentlyContinue
            throw "UI smoke test timed out. See $validationPath"
        }
        $smokeProcess.Refresh()
        if ($smokeProcess.ExitCode -ne 0) { throw "UI smoke test failed with exit code $($smokeProcess.ExitCode). See $validationPath" }
        if (-not (Test-Path -LiteralPath $smokeOutput -PathType Leaf)) { throw "UI smoke test did not write $smokeOutput" }
        Write-Host "UI smoke report: $smokeOutput"
    }
    else { Write-Warning 'UI smoke validation was explicitly skipped.' }

    # Each ZIP contains only files from this fresh publish, not leftovers from previous builds.
    Compress-Archive -Path (Join-Path $stagingPath '*') -DestinationPath $temporaryZip -CompressionLevel Optimal
    if (Test-Path -LiteralPath $publishPath) {
        # Both resolved move targets were checked against .artifacts above; retain the previous output.
        Move-Item -LiteralPath $publishPath -Destination $previousPath
        Write-Host "Previous publish retained: $previousPath"
    }
    Move-Item -LiteralPath $stagingPath -Destination $publishPath
    Move-Item -LiteralPath $temporaryZip -Destination $packagePath -Force
    Write-Host "Executable: $(Join-Path $publishPath 'ResourceStatus.exe')"
    Write-Host "Package: $packagePath"
}
finally { Pop-Location }
