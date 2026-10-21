[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$version = '3.2.8'
$expected = 'E6B89A6DA0FE7DA5F64302306153C6AAC7A4BE67C800426A91AE014093E4DF2D'
$root = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\perfview\$version"
$path = Join-Path $root 'PerfView.exe'
$url = "https://github.com/microsoft/perfview/releases/download/v$version/PerfView.exe"
New-Item -ItemType Directory -Path $root -Force | Out-Null
if (-not (Test-Path -LiteralPath $path)) {
    $temporary = Join-Path $root ('download-' + [Guid]::NewGuid().ToString('N') + '.exe')
    try {
        Invoke-WebRequest -Uri $url -OutFile $temporary
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $expected) {
            throw 'The official release digest did not match. No executable was installed.'
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $temporary
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike 'CN=Microsoft Corporation,*') {
            throw 'Microsoft Authenticode verification failed. No executable was installed.'
        }
        Move-Item -LiteralPath $temporary -Destination $path
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}
if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) {
    throw 'Existing PerfView does not match the pinned release; it was not overwritten.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $path
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike 'CN=Microsoft Corporation,*') {
    throw 'The existing PerfView executable does not have a valid Microsoft signature.'
}
Write-Output "Verified Microsoft PerfView $version at $path. No profiler, UAC, or EULA acceptance was started."
