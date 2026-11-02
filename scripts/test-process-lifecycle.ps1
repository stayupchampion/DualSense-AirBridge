[CmdletBinding()]
param([string]$BuildDirectory='src\DsBridge.App\bin\Release\net9.0-windows')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build=[IO.Path]::GetFullPath((Join-Path $root $BuildDirectory))
if(!$build.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Test build must stay inside the project'}
$info=[Diagnostics.ProcessStartInfo]::new((Join-Path $build 'DualSenseBridge.exe'))
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.WorkingDirectory=$build
$info.Arguments='--process-check'
$process=[Diagnostics.Process]::Start($info)
try {
    if(!$process.WaitForExit(45000)){$process.Kill();$process.WaitForExit();throw 'Synthetic process checks timed out'}
    if($process.ExitCode -ne 0){throw "Process checks failed; see $build\.local\last-error.txt"}
    $result=Get-Content -LiteralPath (Join-Path $build '.local\process-check.json') -Raw | ConvertFrom-Json
    if(!$result.processAndHidHideChecks -or !$result.recoveryEvidenceChecks -or $result.hardwareApisCalled){throw 'Incomplete process checks'}
    Write-Output 'Process lifecycle and HidHide snapshot checks passed (synthetic; no devices).'
} finally {$process.Dispose()}
