param(
    [Parameter(Mandatory = $true)][int]$TargetProcessId,
    [int]$Seconds = 600,
    [string]$Output = (Join-Path $PSScriptRoot '..\.artifacts\validation\widget-performance.json')
)
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
$process = Get-Process -Id $TargetProcessId
$process.Refresh()
$initialCpu = $process.TotalProcessorTime.TotalSeconds
$timer = [Diagnostics.Stopwatch]::StartNew()
$peakPrivateMiB = 0.0
$count = 0
try {
    while ($timer.Elapsed.TotalSeconds -lt $Seconds) {
        Start-Sleep -Seconds 1
        $process.Refresh()
        if ($process.HasExited) { throw 'The widget exited during measurement.' }
        $count++
        $memory = $process.PrivateMemorySize64 / 1MB
        $peakPrivateMiB = [Math]::Max($peakPrivateMiB, $memory)
        if ($count % 10 -eq 0 -or $timer.Elapsed.TotalSeconds -ge $Seconds) {
            $elapsed = $timer.Elapsed.TotalSeconds
            $report = [ordered]@{
                Complete = ($elapsed -ge $Seconds)
                ProcessId = $TargetProcessId
                ProcessName = $process.ProcessName
                ElapsedSeconds = [Math]::Round($elapsed, 2)
                LogicalProcessors = [Environment]::ProcessorCount
                CpuPercentOfMachine = [Math]::Round(($process.TotalProcessorTime.TotalSeconds - $initialCpu) / $elapsed / [Environment]::ProcessorCount * 100, 4)
                PrivateMemoryMiB = [Math]::Round($memory, 2)
                PeakPrivateMemoryMiB = [Math]::Round($peakPrivateMiB, 2)
                Note = 'Entire running widget process, including native collection and WPF update work. Screen-capture availability must be reported separately.'
            }
            $report | ConvertTo-Json | Set-Content -LiteralPath $outputPath -Encoding UTF8
        }
    }
    Get-Content -LiteralPath $outputPath
}
finally { $process.Dispose() }
