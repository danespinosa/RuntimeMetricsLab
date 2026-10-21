[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Registration,
    [Parameter(Mandatory = $true)][guid]$JobId,
    [switch]$AcceptPerfViewSystemTrace
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This advanced launcher requires native Windows PowerShell 7.' }
if (-not $AcceptPerfViewSystemTrace -or $JobId -eq [guid]::Empty) {
    throw 'No elevation was requested. Explicit scope consent and the current waiting job ID are required. Prefer the in-app approval step.'
}
$path = (Resolve-Path -LiteralPath $Registration).Path
$item = Get-Item -LiteralPath $path
if ($item.Length -gt 16384 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid registration file.' }
$registrationData = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
if ($registrationData.SchemaVersion -ne 3 -or $registrationData.LabId -notmatch '^[a-f0-9]{10}$') { throw 'Expired registration format; fresh stock PerfView scope consent is required.' }
$expected = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "RuntimeMetricsLab\Attribution\$($registrationData.LabId)\collector-registration.json"
if ($path -ne $expected) { throw 'Registration is outside this user-local lab.' }
$control = [uri]$registrationData.ControlUrl
if (-not $control.IsLoopback -or $control.Scheme -notin @('http', 'https')) { throw 'No fixed loopback control endpoint.' }
$scope = 'Stock Microsoft PerfView 3.2.8 PERSISTS system-wide kernel CPU/process/thread/image events (including other process command lines, paths and stacks) in a private local ETL. FocusProcess scopes non-kernel providers only; offline method reports select the lab target. Only the authenticated launcher and stock PerfView run elevated; workload/UI/analysis remain non-elevated. Consent can be reused for at most one hour. You must personally approve Windows UAC and any required PerfView EULA. Work cancellation is immediate; PerfView finishes its bounded collection/rundown/merge. Late approval cannot resume a cancelled job.'
if (-not $PSCmdlet.ShouldContinue($scope, 'Accept profiler scope and request one Windows UAC prompt')) {
    Write-Output 'No elevation was requested.'
    return
}
$body = @{ registrationGeneration = $registrationData.Generation } | ConvertTo-Json
$headers = @{ 'X-Lab-Key' = $registrationData.ControlKey }
try {
    $response = Invoke-RestMethod -Method Post -Uri "$($control.AbsoluteUri.TrimEnd('/'))/windows/approvals/$JobId/accept" `
        -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 10
    Write-Output "Approval admitted for job $JobId. Return to the app and respond to the collector's Windows UAC dialog."
}
finally { $headers.Clear(); $registrationData = $null }
