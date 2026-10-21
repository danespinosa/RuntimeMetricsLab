param(
    [Parameter(Mandatory = $true)][uri]$BaseUrl,
    [Parameter(Mandatory = $true)][uri]$CollectorUrl,
    [Parameter(Mandatory = $true)][uri]$TargetUrl,
    [Parameter(Mandatory = $true)][string]$LabId,
    [Parameter(Mandatory = $true)][switch]$AllowContainerFaults,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\attribution-faults')
)

$ErrorActionPreference = 'Stop'
if (-not $AllowContainerFaults) { throw 'Explicit -AllowContainerFaults is required.' }
foreach ($url in @($BaseUrl, $CollectorUrl, $TargetUrl)) {
    if (-not $url.IsLoopback -or $url.Scheme -notin @('http', 'https')) { throw 'Only local loopback lab URLs are accepted.' }
}
if ($LabId -notmatch '^[a-f0-9]{10}$') { throw 'Expected the exact ten-character worktree lab ID.' }
$env:DOCKER_HOST = 'tcp://127.0.0.1:2375'
$root = $BaseUrl.AbsoluteUri.TrimEnd('/')
$collectorRoot = $CollectorUrl.AbsoluteUri.TrimEnd('/')
$targetRoot = $TargetUrl.AbsoluteUri.TrimEnd('/')
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$evidence = [System.Collections.Generic.List[object]]::new()
$names = @(docker ps --filter "label=runtimemetricslab.lab=$LabId" --format '{{.Names}}')
$collector = @($names | Where-Object { $_ -like 'attribution-collector-*' })
$web = @($names | Where-Object { $_ -like 'client-linux-*' })
$target = "rml-target-$LabId"
if ($collector.Count -ne 1 -or $web.Count -ne 1 -or $target -notin $names) { throw 'The exact isolated lab must be running.' }
$collector = $collector[0]
$web = $web[0]

function Invoke-LabDocker([string[]]$arguments) {
    $result = & docker.exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Scoped Docker operation failed: $($arguments[0])" }
    return $result
}
function Record([string]$name, $details) {
    $evidence.Add([pscustomobject]@{ Name = $name; Status = 'passed'; Details = $details })
    $evidence | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'fault-results.json')
    Write-Host "$name PASSED"
}
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Http([string]$method, [string]$url, $body = $null, $headers = @{}) {
    $parameters = @{ Method = $method; Uri = $url; Headers = $headers; TimeoutSec = 15; MaximumRedirection = 0 }
    if ($null -ne $body) { $parameters.ContentType = 'application/json'; $parameters.Body = $body | ConvertTo-Json -Depth 10 -Compress }
    Invoke-RestMethod @parameters
}
function Expect-Status([int]$status, [scriptblock]$action) {
    try { & $action | Out-Null }
    catch { if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq $status) { return }; throw }
    throw "Expected HTTP $status."
}
function Start-JobRun([int]$seconds = 4, [int]$workers = 1) {
    Http POST "$root/api/attribution/jobs" @{
        idempotencyKey = [guid]::NewGuid()
        settings = @{ lesson = 'cpu'; workers = $workers; durationSeconds = $seconds; allocationMiB = 64; retainedMiB = 16 }
    }
}
function Wait-Phase([guid]$id, [scriptblock]$predicate, [int]$seconds = 50) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        $job = Http GET "$root/api/attribution/jobs/$id"
        if (& $predicate $job) { return $job }
        if ($job.phase -in @('ready', 'partial', 'failed', 'cancelled')) { throw "Job ended before requested phase: $($job.phase) $($job.error)" }
        if ($clock.Elapsed.TotalSeconds -gt $seconds) { throw "Job $id phase deadline expired." }
        Start-Sleep -Milliseconds 20
    } while ($true)
}
function Wait-End([guid]$id) {
    Wait-Phase $id { param($job) $job.phase -in @('ready', 'partial', 'failed', 'cancelled') } 150
}
function Ready-Run {
    $job = Start-JobRun
    $done = Wait-End $job.id
    Assert ($done.phase -eq 'ready') "Recovery did not permit another real capture: $($done.phase) $($done.error)"
    return $done
}

# Read credentials only into this operator process; never echo or persist them.
$metadata = Invoke-LabDocker @('inspect', $collector) | ConvertFrom-Json
$credential = @($metadata[0].Config.Env | Where-Object { $_.StartsWith('Attribution__CollectorKey=') })[0].Split('=', 2)[1]
$headers = @{ 'X-Lab-Key' = $credential }
Expect-Status 401 { Http GET "$collectorRoot/capabilities" }
Expect-Status 401 { Http GET "$targetRoot/api/attribution/status" }
Expect-Status 400 { Http POST "$collectorRoot/captures" @{ operationId = [guid]::NewGuid(); target = 'machine-wide'; durationSeconds = 5 } $headers }
Expect-Status 400 { Http POST "$collectorRoot/captures" @{ operationId = [guid]::NewGuid(); target = 'linux-workload'; durationSeconds = 5; processId = 2 } $headers }
Expect-Status 400 { Http POST "$collectorRoot/captures" @{ operationId = [guid]::NewGuid(); target = 'linux-workload'; durationSeconds = 5; providers = 'arbitrary'; output = '/tmp/other' } $headers }
Expect-Status 409 { Http POST "$targetRoot/api/experiments/cpu-burn/runs" @{} }
Expect-Status 409 { Http GET "$targetRoot/workloads/http/payload?sizeBytes=1024" }
Record 'authentication-fixed-target-and-legacy-gates' 'Missing credentials, machine-wide target, caller PID/providers/path and legacy workloads rejected.'
$credential = $null; $headers.Clear(); $metadata = $null

# Read-only capture storage must fail admission without claiming a permanent lease.
Invoke-LabDocker @('exec', $collector, 'mount', '-o', 'remount,bind,ro', '/captures') | Out-Null
try {
    $job = Start-JobRun
    $failed = Wait-End $job.id
    Assert ($failed.phase -eq 'failed') 'Read-only capture storage unexpectedly admitted a capture.'
}
finally { Invoke-LabDocker @('exec', $collector, 'mount', '-o', 'remount,bind,rw', '/captures') | Out-Null }
$recovered = Ready-Run
Record 'transactional-admission-io-failure' @{ FailedJob = $failed.id; RecoveredJob = $recovered.id }

# A sparse file exercises the byte-budget guard without filling the host filesystem.
$sparse = "/captures/storage-fault-$([guid]::NewGuid().ToString('N'))"
Invoke-LabDocker @('exec', $collector, 'truncate', '-s', '5G', $sparse) | Out-Null
try {
    $capability = Http GET "$root/api/attribution/capabilities"
    Assert (-not $capability.available) 'Storage budget did not reject the oversized artifact.'
}
finally { Invoke-LabDocker @('exec', $collector, 'rm', '--', $sparse) | Out-Null }
Record 'storage-budget' 'New work blocked; only the explicitly created sparse test file was removed.'

# Force only the final atomic rename to fail, not the raw writer.
$job = Start-JobRun 8
$active = Wait-Phase $job.id { param($state) $state.detailPhase -eq 'measuring' }
$collision = "/captures/$(([guid]$active.captureId).ToString('N'))"
Invoke-LabDocker @('exec', $collector, 'mkdir', '--', $collision) | Out-Null
try {
    $failed = Wait-End $job.id
    Assert ($failed.phase -eq 'failed') 'Final rename collision was not surfaced.'
}
finally { Invoke-LabDocker @('exec', $collector, 'rmdir', '--', $collision) | Out-Null }
$recovered = Ready-Run
Record 'atomic-finalization-io-failure' @{ FailedJob = $failed.id; RecoveredJob = $recovered.id; RawPartialDirectory = "$collision.partial" }

foreach ($phase in @('baseline', 'measuring', 'finalizing', 'analyzing')) {
    $job = Start-JobRun 5 2
    $null = Wait-Phase $job.id {
        param($state)
        if ($phase -in @('baseline', 'measuring')) { $state.detailPhase -eq $phase } else { $state.phase -eq $phase }
    }
    Http POST "$root/api/attribution/jobs/$($job.id)/cancel" @{} | Out-Null
    $cancelled = Wait-End $job.id
    Assert ($cancelled.phase -eq 'cancelled') "Cancellation during $phase was not durable."
    Record "cancellation-$phase" $cancelled.id
}

$job = Start-JobRun 20
$null = Wait-Phase $job.id { param($state) $state.detailPhase -eq 'measuring' }
Invoke-LabDocker @('kill', '--signal', 'SIGKILL', $web) | Out-Null
aspire resource client-linux start --non-interactive
if ($LASTEXITCODE -ne 0) { throw 'Aspire could not restart the crashed web resource.' }
aspire wait client-linux --non-interactive
if ($LASTEXITCODE -ne 0) { throw 'Web resource did not recover.' }
$interrupted = Wait-End $job.id
Assert ($interrupted.phase -eq 'failed') 'Interrupted durable job was not recovered as failed.'
$recovered = Ready-Run
Record 'web-crash-restart' @{ InterruptedJob = $interrupted.id; RecoveredJob = $recovered.id }

$job = Start-JobRun 20
$null = Wait-Phase $job.id { param($state) $state.detailPhase -eq 'measuring' }
Invoke-LabDocker @('kill', '--signal', 'SIGKILL', $collector) | Out-Null
aspire resource attribution-collector start --non-interactive
if ($LASTEXITCODE -ne 0) { throw 'Aspire could not restart the crashed collector.' }
aspire wait attribution-collector --non-interactive
if ($LASTEXITCODE -ne 0) { throw 'Collector resource did not recover.' }
$interrupted = Wait-End $job.id
Assert ($interrupted.phase -eq 'failed') 'Collector crash was not surfaced by the durable workflow.'
$recovered = Ready-Run
Record 'collector-crash-restart' @{ InterruptedJob = $interrupted.id; RecoveredJob = $recovered.id }

Write-Output "Scoped authentication, IO, cancellation and restart failures verified. Evidence: $output"
