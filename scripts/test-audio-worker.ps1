[CmdletBinding()]
param([string]$BuildDirectory='src\DsBridge.App\bin\Release\net9.0-windows')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build=[IO.Path]::GetFullPath((Join-Path $root $BuildDirectory))
if(!$build.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Test build must stay inside the project'}
$exe=Join-Path $build 'DualSenseBridge.exe'
$info=[Diagnostics.ProcessStartInfo]::new($exe)
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.WorkingDirectory=$build
$info.Arguments='--audio-isolation-check'
$process=[Diagnostics.Process]::Start($info)
try {
  if(!$process.WaitForExit(30000)){$process.Kill();$process.WaitForExit();throw 'Synthetic audio worker check timed out'}
  if($process.ExitCode -ne 0){throw "Synthetic audio worker check failed; see $build\.local\last-error.txt"}
  $result=Get-Content -LiteralPath (Join-Path $build '.local\audio-isolation-check.json') -Raw | ConvertFrom-Json
  if(!$result.normalDispose -or !$result.noLateWorkerOrRestart -or !$result.unexpectedExit -or !$result.readOnlyRejectsPolicyWrites -or !$result.ownerExitReapsHelper -or !$result.recoveryObserverFailureIsolated -or $result.deviceApisCalled){throw 'Incomplete audio worker check'}
  Write-Output 'Audio worker fault checks passed (synthetic data only; no audio or controller APIs).'
} finally {$process.Dispose()}
