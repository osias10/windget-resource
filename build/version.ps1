# Shared release version validation for packaging and CI; no build metadata in asset names.
function Get-ReleaseVersionInfo {
    param([Parameter(Mandatory = $true)][string]$Version)
    $number = '(?:0|[1-9][0-9]*)'
    $identifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
    $pattern = "\A(?<core>$number\.$number\.$number)(?:-(?<pre>$identifier(?:\.$identifier)*))?\z"
    $match = [regex]::Match($Version, $pattern)
    if (-not $match.Success -or $Version.Length -gt 128) {
        throw "Version must be major.minor.patch with an optional SemVer prerelease suffix, found '$Version'."
    }
    $numeric = [Version]($match.Groups['core'].Value + '.0')
    if ($numeric.Major -gt 65534 -or $numeric.Minor -gt 65534 -or $numeric.Build -gt 65534) {
        throw 'Version components must fit Windows assembly and installer version fields (0..65534).'
    }
    [pscustomobject]@{
        Version = $Version
        FileVersion = $numeric
        IsPrerelease = $match.Groups['pre'].Success
    }
}
