param(
    [Parameter(Mandatory = $true)][uri]$BaseUrl,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\attribution-validation')
)

$ErrorActionPreference = 'Stop'
if (-not $BaseUrl.IsLoopback -or $BaseUrl.Scheme -notin @('http', 'https')) {
    throw 'Validation is restricted to a local loopback lab URL discovered through Aspire.'
}
$root = $BaseUrl.AbsoluteUri.TrimEnd('/')
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$results = [System.Collections.Generic.List[object]]::new()

function Request([string]$method, [string]$path, $body = $null) {
    $parameters = @{ Method = $method; Uri = "$root/api/attribution/$path"; TimeoutSec = 20 }
    if ($null -ne $body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $body | ConvertTo-Json -Depth 10 -Compress
    }
    Invoke-RestMethod @parameters
}

function Assert([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Expect-Status([int]$status, [scriptblock]$action) {
    try {
        & $action | Out-Null
    }
    catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq $status) { return }
        throw
    }
    throw "Expected HTTP $status."
}

function Wait-Job([guid]$id) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        $job = Request GET "jobs/$id"
        if ($job.phase -in @('ready', 'partial', 'cancelled', 'failed')) { return $job }
        if ($clock.Elapsed.TotalSeconds -gt 150) {
            Request POST "jobs/$id/cancel" @{} | Out-Null
            throw "Job $id exceeded the finite validation deadline."
        }
        Start-Sleep -Milliseconds 200
    } while ($true)
}

function Settings([string]$lesson, [int]$workers, [int]$duration, [int]$retained = 16) {
    @{ lesson = $lesson; workers = $workers; durationSeconds = $duration; allocationMiB = 64; retainedMiB = $retained }
}

function Run-Lesson([string]$name, [hashtable]$settings) {
    $started = Request POST jobs @{ idempotencyKey = [guid]::NewGuid(); settings = $settings }
    $job = Wait-Job $started.id
    Assert ($job.phase -eq 'ready') "$name did not produce a ready report: $($job.phase) $($job.error)"
    $report = Request GET "jobs/$($job.id)/report"
    Assert ($report.Window.Complete) "$name did not have a complete measurement window."
    Assert ($report.Process.CpuSeconds -gt 0) "$name has no measured CPU total."
    if ($settings.lesson -in @('cpu', 'mixed')) {
        Assert ($report.Process.CpuSamplingRatio -ge 0.75 -and $report.Process.CpuSamplingRatio -le 1.25) "$name independent CPU calibration is outside the quality guard."
    }
    elseif ($report.Process.CpuSamplingRatio -lt 0.75 -or $report.Process.CpuSamplingRatio -gt 1.25) {
        Assert (@($report.Methods | Where-Object { $null -ne $_.CpuMillisecondsPerCall }).Count -eq 0) "$name published normal CPU per-call costs despite insufficient sparse-CPU calibration."
    }
    Assert ($null -ne $report.Process.CoveredCpuSeconds) "$name has no supported covered CPU pool."
    $selfSum = ($report.Methods | Measure-Object SelfAverageCores -Sum).Sum
    Assert ([math]::Abs($selfSum + $report.Process.UncoveredCores - $report.Process.AverageCores) -lt 1e-8) "$name exclusive cores do not reconcile."
    $labs = @($report.Methods | Where-Object Category -eq 'lab')
    $method = $labs | Sort-Object SelfAverageCores -Descending | Select-Object -First 1
    if ($settings.lesson -eq 'cpu') {
        $arithmetic = @($labs | Where-Object Method -Like '*::Arithmetic(*')
        Assert ($arithmetic.Count -eq 1) "$name did not resolve exactly one counted Arithmetic identity."
        $method = $arithmetic[0]
        Assert ($method.SelfAverageCores -gt $report.Process.AverageCores * 0.65) "$name named arithmetic does not dominate CPU."
        Assert ($method.CompletedCalls -gt 0 -and $method.CpuMillisecondsPerCall -gt 0) "$name has no supported CPU unit cost."
        if ($settings.workers -gt 1) { Assert ($report.Process.AverageCores -gt 1) "$name did not demonstrate multicore consumption; inspect inherited restrictions." }
    }
    if ($settings.lesson -eq 'allocation') {
        Assert ($report.Process.AllocationSamplingRatio -ge 0.75 -and $report.Process.AllocationSamplingRatio -le 1.25) "$name independent allocation calibration is outside the quality guard."
        $large = @($labs | Where-Object Method -Like '*::AllocateLarge(*')
        Assert ($large.Count -eq 1) "$name did not resolve exactly one counted AllocateLarge identity."
        $method = $large[0]
        Assert ($method.AllocatedBytesPerCall -ge 96KB -and $method.AllocatedBytesPerCall -le 160KB) "$name sampled allocation unit cost is not consistent with the controlled 128 KiB allocation."
        Assert ($method.SameThreadAllocatedBytes -gt 0) "$name has no independent same-thread allocation evidence."
    }
    if ($settings.lesson -eq 'mixed') {
        $waiting = @($labs | Where-Object Method -Match 'WaitStep')
        Assert ($waiting.Count -eq 1 -and $waiting[0].CompletedCalls -gt 0) 'Mixed lesson lost the known waiting method.'
        Assert ($null -eq $waiting[0].SelfAverageCores -or $waiting[0].SelfAverageCores -lt $report.Process.AverageCores * 0.1) 'Waiting was charged as sustained on-CPU work.'
        Assert (($labs | Where-Object Method -Like '*::AllocateSmall(*' | Measure-Object AllocatedBytes -Sum).Sum -gt 0) 'Mixed lesson has no independent allocation-hot site.'
    }
    if ($settings.lesson -eq 'retention') {
        Assert (@($report.Samples | Where-Object Phase -eq 'explicit-gc-after-release').Count -gt 0) 'Retention lesson did not preserve its labeled recovery phase.'
    }
    $path = Join-Path $output "$name-report.json"
    Invoke-WebRequest "$root/api/attribution/jobs/$($job.id)/report" -OutFile $path -TimeoutSec 30
    $results.Add([pscustomobject]@{
        Name = $name; JobId = $job.id; CaptureId = $job.captureId; Revision = $report.Revision
        Seconds = $report.Process.Seconds; ProcessCores = $report.Process.AverageCores
        Method = $method.Method; MethodCores = $method.SelfAverageCores; Calls = $method.CompletedCalls
        SelfCpuMillisecondsPerCall = $method.CpuMillisecondsPerCall
        EstimatedAllocatedBytes = $method.AllocatedBytes; AllocationBytesPerSecond = $method.AllocationBytesPerSecond
        AllocatedBytesPerCall = $method.AllocatedBytesPerCall
        CpuCalibrationRatio = $report.Process.CpuSamplingRatio
        AllocationCalibrationRatio = $report.Process.AllocationSamplingRatio
        ParserEventsLost = $report.EventsLost; KernelLossReported = ($null -ne $report.Collector.KernelLostEvents)
    })
    $results | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'summary.json')
    $methodCores = if ($null -eq $method.SelfAverageCores) { 'unavailable' } else { [math]::Round($method.SelfAverageCores, 3) }
    Write-Host "$name READY: $($job.id), process $([math]::Round($report.Process.AverageCores,3)) cores; selected method $methodCores cores."
    return $job
}

$capability = Request GET capabilities
Assert $capability.available "Attribution preflight is unavailable: $($capability.error)"
Expect-Status 400 { Request POST jobs @{ idempotencyKey = [guid]::NewGuid(); settings = (Settings cpu 0 3) } }
Expect-Status 400 { Request POST jobs @{ idempotencyKey = [guid]::NewGuid(); settings = (Settings cpu 1 61) } }

1..3 | ForEach-Object { Run-Lesson "cpu-single-$_" (Settings cpu 1 4) | Out-Null }
Run-Lesson 'cpu-multicore' (Settings cpu 4 4) | Out-Null
1..3 | ForEach-Object { Run-Lesson "allocation-$_" (Settings allocation 2 4) | Out-Null }
Run-Lesson 'mixed' (Settings mixed 2 4) | Out-Null
$retention = Run-Lesson 'retention' (Settings retention 1 3 8)

$key = [guid]::NewGuid()
$request = @{ idempotencyKey = $key; settings = (Settings mixed 1 15) }
$first = Request POST jobs $request
$duplicate = Request POST jobs $request
Assert ($first.id -eq $duplicate.id) 'Duplicate idempotent start created another job.'
Expect-Status 400 { Request POST jobs @{ idempotencyKey = $key; settings = (Settings cpu 1 15) } }
Expect-Status 409 { Request POST jobs @{ idempotencyKey = [guid]::NewGuid(); settings = (Settings cpu 1 3) } }
Request POST "jobs/$($first.id)/cancel" @{} | Out-Null
$cancelled = Wait-Job $first.id
Assert ($cancelled.phase -eq 'cancelled') 'Explicit cancellation did not reach a durable cancelled state.'

$bundlePath = Join-Path $output 'retention-bundle.zip'
Invoke-WebRequest "$root/api/attribution/jobs/$($retention.id)/bundle" -OutFile $bundlePath -TimeoutSec 60
$zip = [System.IO.Compression.ZipFile]::OpenRead($bundlePath)
try {
    foreach ($name in @('job.json', 'bundle.json', 'target.json', 'trace.nettrace', 'collector.json', 'stdout.log', 'stderr.log')) {
        Assert ($null -ne $zip.GetEntry($name)) "Bundle is missing $name."
    }
    $reader = [System.IO.StreamReader]::new($zip.GetEntry('target.json').Open())
    try { $target = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    Assert ($target.RetainedPayloadBytes -eq 8MB) 'Controlled retained payload accounting is incorrect.'
}
finally { $zip.Dispose() }

Write-Output "Real captures, numeric reconciliation, independent calibration, all four lessons, idempotency, overlap, cancellation and bundle download verified. Evidence: $output"
