param(
    [Parameter(Mandatory = $true)][uri]$BaseUrl,
    [Parameter(Mandatory = $true)][switch]$CounterDiagnosticsOnly,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\windows-diagnostics')
)

$ErrorActionPreference = 'Stop'
if (-not $CounterDiagnosticsOnly -or -not $BaseUrl.IsLoopback) {
    throw 'This script verifies only non-elevated counter diagnostics through an explicit local URL. It never authorizes ETW.'
}
$root = $BaseUrl.AbsoluteUri.TrimEnd('/')
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
function Request([string]$method, [string]$path, $body = $null) {
    $options = @{ Method = $method; Uri = "$root/api/attribution/$path"; TimeoutSec = 15 }
    if ($null -ne $body) { $options.ContentType = 'application/json'; $options.Body = $body | ConvertTo-Json -Depth 8 }
    Invoke-RestMethod @options
}
function Wait-End([guid]$id) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 100
        $job = Request GET "jobs/$id"
        if ($clock.Elapsed.TotalSeconds -gt 30) { Request POST "jobs/$id/cancel" @{} | Out-Null; throw 'Native diagnostic deadline exceeded.' }
    } while ($job.phase -notin @('diagnostic-ready', 'failed', 'cancelled'))
    return $job
}
$capability = Request GET capabilities
if ($capability.platform -ne 1) { throw 'Expected the native Windows attribution topology.' }
$summary = @()
foreach ($preset in @(@{lesson='cpu';workers=1}, @{lesson='cpu';workers=4}, @{lesson='allocation';workers=2},
                       @{lesson='retention';workers=1}, @{lesson='mixed';workers=2})) {
    $request = @{ idempotencyKey=[guid]::NewGuid();kind=1;settings=@{
        lesson=$preset.lesson;workers=$preset.workers;durationSeconds=2;allocationMiB=32;retainedMiB=8
    }}
    $job = Request POST jobs $request
    $duplicate = Request POST jobs $request
    if ($job.id -ne $duplicate.id) { throw 'Counter diagnostic idempotency failed.' }
    $done = Wait-End $job.id
    if ($done.phase -ne 'diagnostic-ready') { throw "Native diagnostic failed: $($done.error)" }
    $report = Request GET "jobs/$($job.id)/counter-diagnostic"
    if ($report.measurement.identity.platform -ne 1 -or -not $report.measurement.window.complete -or
        $report.seconds -lt 2 -or $report.processAverageCores -le 0) { throw 'Missing real native counter evidence.' }
    if ($preset.lesson -eq 'cpu' -and $preset.workers -eq 4 -and $report.processAverageCores -lt 1) {
        throw 'Multicore diagnostic did not demonstrate more than one core; inspect inherited restrictions.'
    }
    $calls = @($report.measurement.calls | Where-Object completed -gt 0)
    if ($calls.Count -eq 0) { throw 'No instrumented calls were recorded.' }
    if ($preset.lesson -eq 'retention' -and $report.measurement.retainedPayloadBytes -ne 8MB) {
        throw 'Controlled retention accounting failed.'
    }
    if ($preset.lesson -eq 'mixed' -and @($calls | Where-Object method -eq 'WaitStep').Count -ne 1) {
        throw 'Waiting method accounting disappeared.'
    }
    $name="$($preset.lesson)-$($preset.workers)"
    Invoke-WebRequest "$root/api/attribution/jobs/$($job.id)/counter-diagnostic" -OutFile (Join-Path $OutputDirectory "$name.json")
    $summary += @{ name=$name;jobId=$job.id;processCores=$report.processAverageCores;seconds=$report.seconds;
        allocatedBytes=$report.allocatedBytes;calls=$calls;cpuMethodCosts='UNAVAILABLE: no ETW on-CPU collection authorized' }
    Write-Host "$name counter-only diagnostic passed."
}
$request = @{idempotencyKey=[guid]::NewGuid();kind=1;settings=@{lesson='cpu';workers=1;durationSeconds=15;allocationMiB=32;retainedMiB=8}}
$job = Request POST jobs $request
Request POST "jobs/$($job.id)/cancel" @{} | Out-Null
$done = Wait-End $job.id
if($done.phase -ne 'cancelled') { throw 'Native diagnostic cancellation did not persist.' }
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputDirectory 'summary.json')
Write-Output "Native workloads, counters, same-thread allocation, retention, waiting, idempotency and cancellation verified. No Windows CPU method profile was collected. Evidence: $OutputDirectory"
