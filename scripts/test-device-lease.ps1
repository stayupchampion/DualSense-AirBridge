$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$exe=Join-Path $root 'dist\DualSenseBridge\DualSenseBridge.exe'
function Invoke-Probe {
    $probe=Start-Process -FilePath $exe -ArgumentList '--test-lease-probe' -WindowStyle Hidden -PassThru
    try {
        if(-not $probe.WaitForExit(10000)){$probe.Kill();throw 'Lease probe timed out'}
        return $probe.ExitCode
    } finally {$probe.Dispose()}
}
$initial=Invoke-Probe
if($initial -ne 0){throw 'Another controller test is running; leave it untouched and retry after it finishes.'}
$owned=[System.Threading.Semaphore]::new(1,1,'Local\DsBridge-HardwareTests')
$held=$false
try {
    $held=$owned.WaitOne(0)
    if(-not $held){throw 'Controller test became active; no hardware was touched.'}
    $blocked=Invoke-Probe
    if($blocked -ne 2){throw "Concurrent test was not rejected: $blocked"}
    $null=$owned.Release();$held=$false
    $released=Invoke-Probe
    if($released -ne 0){throw "Lease did not release: $released"}
} finally {
    if($held){$null=$owned.Release()}
    $owned.Dispose()
}
$result=[ordered]@{Timestamp=(Get-Date -Format o);Initial=$initial;WhileAnotherProcessOwnsLease=$blocked;AfterRelease=$released;Passed=$true;HardwareTouched=$false}
$path=Join-Path $root '.local\tests\device-test-lease.json'
$null=New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force
$result | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding UTF8
$result | ConvertTo-Json
