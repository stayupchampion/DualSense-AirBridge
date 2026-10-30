param([int]$ActiveSeconds=8,[switch]$Feedback,[int]$ObservationSeconds=0,[switch]$LatencyPulse,[string]$Executable='')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Executable) { $Executable=Join-Path $root 'dist\DualSenseBridge\DualSenseBridge.exe' }
$exe=[IO.Path]::GetFullPath($Executable)
if(!$exe.StartsWith(((Join-Path $root 'dist') + '\'),[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($exe)-ne 'DualSenseBridge.exe') { throw 'Hardware test executable must be a project dist build' }
$hide='C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe'
$usbip='C:\Program Files\USBip\usbip.exe'
$utf8=New-Object Text.UTF8Encoding($false)
$testId=[Guid]::NewGuid().ToString('N')
$testDir=Join-Path $root ('.local\tests\hardware-'+$testId)
$session=Join-Path (Split-Path $exe) ('.local\sessions\'+$testId)
New-Item -ItemType Directory -Path $testDir,$session -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $root '.local\tests\latest-hardware.txt'),$testDir,$utf8)
$receipt=[ordered]@{StartedAt=(Get-Date).ToString('o');Status='checking';Session=$session;ActiveSeconds=$ActiveSeconds;ReachedActive=$false;Error=$null;AppSha256=(Get-FileHash -LiteralPath $exe).Hash;BackendSha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path $exe) 'backend\vdsd.exe')).Hash}
function Save { [IO.File]::WriteAllText((Join-Path $testDir 'result.json'),($receipt|ConvertTo-Json -Depth 10),$utf8) }
function Diagnose($Name) {
  $path=Join-Path (Split-Path $exe) ('.local\diagnostics\hardware-'+$testId+'-'+$Name+'.json')
  $p=Start-Process -FilePath $exe -ArgumentList @('--diagnose','--output',('"'+$path+'"')) -WindowStyle Hidden -PassThru
  if(!$p.WaitForExit(15000)){throw 'Diagnosis timed out'}
  if($p.ExitCode-ne 0){throw 'Diagnosis failed'}
  Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}
function HideRead($Argument) {
  for($attempt=0;$attempt-lt 5;$attempt++){
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$hide;$start.Arguments=$Argument;$start.UseShellExecute=$false
    $start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    if(!$process.WaitForExit(5000)){throw 'HidHide read timed out'}
    $output=$stdout.Result;$errorOutput=$stderr.Result;$code=$process.ExitCode;$process.Dispose()
    if($code-eq 0){return $output.TrimEnd()}
    if($errorOutput-notmatch '0x0005' -or $attempt-eq 4){throw ('HidHide read failed: '+$errorOutput+$output)}
    Start-Sleep -Milliseconds (125*($attempt+1))
  }
}
function Snapshot {
  $states=@('--cloak-state','--inv-state','--dev-list','--app-list') | ForEach-Object {
    $o=HideRead $_
    [pscustomobject]@{Argument=$_;Output=($o -join [Environment]::NewLine)}
  }
  $ports=& $usbip port 2>&1
  if($LASTEXITCODE-ne 0){throw 'USBip port read failed'}
  [pscustomobject]@{HidHide=$states;Ports=($ports -join [Environment]::NewLine);Devices=@(Get-PnpDevice -PresentOnly | Where-Object {$_.InstanceId -match 'VID_054C&PID_0CE6|VID&0002054C_PID&0CE6'} | Select-Object Status,Class,FriendlyName,InstanceId)}
}
$worker=$null
try {
  Save
  $before=Diagnose 'before'
  if($before.Blockers.Count-ne 0){throw ($before.Blockers-join '; ')}
  $controllers=@($before.Controllers|Where-Object {$_.Transport-eq '蓝牙'})
  if($controllers.Count-ne 1){throw 'Expected exactly one Bluetooth DualSense'}
  $receipt.BeforeProbe=$before
  $receipt.Before=Snapshot
  $parent=Get-Process -Id $PID
  $request=[ordered]@{Controller=$controllers[0];ParentPid=$PID;ParentStarted=$parent.StartTime.ToUniversalTime().Ticks}
  [IO.File]::WriteAllText((Join-Path $session 'request.json'),($request|ConvertTo-Json -Depth 5),$utf8)
  $receipt.Status='starting';Save
  $worker=Start-Process -FilePath $exe -ArgumentList @('--worker',('"'+$session+'"')) -WindowStyle Hidden -PassThru
  $deadline=(Get-Date).AddSeconds(45)
  while((Get-Date)-lt $deadline) {
    $statusPath=Join-Path $session 'status.json'
    if(Test-Path -LiteralPath $statusPath){
      $s=Get-Content -LiteralPath $statusPath -Raw|ConvertFrom-Json
      $receipt.LastStatus=$s;Save
      if($s.State-eq 'active'){$receipt.ReachedActive=$true;break}
      if($s.State-in @('stopped','blocked','recovery-required','restored')){throw ('Startup: '+$s.Message)}
    }
    if($worker.HasExited){throw ('Worker exited: '+$worker.ExitCode)}
    Start-Sleep -Milliseconds 300
  }
  if(!$receipt.ReachedActive){throw 'No active session within bounded startup window'}
  $receipt.Status='active';Save
  $receipt.Active=Snapshot
  $receipt.ActiveProbe=Diagnose 'active'
  Start-Sleep -Seconds ([Math]::Min([Math]::Max($ActiveSeconds,1),30))
  if($Feedback) {
    $receipt.FeedbackResults=@()
    foreach($kind in @('Haptics','Trigger')) {
      $test=Start-Process -FilePath $exe -ArgumentList @('--feedback-test',$kind) -WindowStyle Hidden -PassThru
      if(!$test.WaitForExit(15000)){throw ('Feedback test timeout: '+$kind)}
      if($test.ExitCode-ne 0){throw ('Feedback test failed: '+$kind+'; '+(Get-Content -LiteralPath (Join-Path (Split-Path $exe) '.local\last-error.txt') -Raw))}
      $receipt.FeedbackResults+=Get-Content -LiteralPath (Join-Path (Split-Path $exe) '.local\feedback-cli-last.json') -Raw|ConvertFrom-Json
      Save
    }
    $test=Start-Process -FilePath $exe -ArgumentList '--feedback-test Trigger --cancel-after 350' -WindowStyle Hidden -PassThru
    if(!$test.WaitForExit(10000)-or $test.ExitCode-ne 0){throw 'Feedback cancellation test failed'}
    $receipt.FeedbackCancel=Get-Content -LiteralPath (Join-Path (Split-Path $exe) '.local\feedback-cli-last.json') -Raw|ConvertFrom-Json
    $test=Start-Process -FilePath $exe -ArgumentList @('--feedback-ui-check',('"'+(Join-Path $root '.local\feedback-panel.png')+'"')) -WindowStyle Hidden -PassThru
    if(!$test.WaitForExit(10000)-or $test.ExitCode-ne 0){throw 'Feedback panel render failed'}
  }
  if($LatencyPulse) {
    $capture=Join-Path $session 'latency-pulse.json'
    $test=Start-Process -FilePath $exe -ArgumentList @('--latency-haptics-check',('"'+$capture+'"')) -WindowStyle Hidden -PassThru
    if(!$test.WaitForExit(30000)){throw 'Latency pulse test timed out'}
    if($test.ExitCode-ne 0){
      $detail=if(Test-Path -LiteralPath $capture){$failed=Get-Content -LiteralPath $capture -Raw|ConvertFrom-Json;[string]$failed.Status+': '+[string]$failed.Error}else{'No capture saved; inspect the tool last-error.txt'}
      throw ('Latency pulse test failed: '+$detail)
    }
    $pulse=Get-Content -LiteralPath $capture -Raw|ConvertFrom-Json
    $receipt.LatencyPulse=[ordered]@{File=$capture;Status=$pulse.Status;Error=$pulse.Error;Mode=$pulse.Mode;Pulses=$pulse.Commands.Count;Audio=$pulse.AudioDetails}
    Save
    if($pulse.Status-ne 'completed' -or $pulse.Commands.Count-ne 12){throw 'Latency pulse sequence was incomplete'}
  }
  $receipt.StableProbe=Diagnose 'stable'
  $state=Get-Content -LiteralPath (Join-Path $session 'status.json') -Raw|ConvertFrom-Json
  if($state.State-ne 'active'){throw ('Session stopped during hold: '+$state.Message)}
  if(($receipt.ActiveProbe.AudioOutputs-join '|')-ne ($receipt.BeforeProbe.AudioOutputs-join '|') -or
     ($receipt.StableProbe.AudioOutputs-join '|')-ne ($receipt.BeforeProbe.AudioOutputs-join '|')){
    throw 'Default output changed while bridge reported active'
  }
} catch {
  $receipt.Error=$_.Exception.Message
} finally {
  $receipt.Status='stopping';Save
  [IO.File]::WriteAllText((Join-Path $session 'stop.request'),'stop',$utf8)
  if($worker-and !$worker.HasExited){
    if(!$worker.WaitForExit(45000)){$receipt.Error=([string]$receipt.Error+'; Worker cleanup did not finish within 45 seconds.')}
  }
  if(Test-Path -LiteralPath (Join-Path $session 'status.json')){$receipt.FinalStatus=Get-Content -LiteralPath (Join-Path $session 'status.json') -Raw|ConvertFrom-Json}
  if(Test-Path -LiteralPath (Join-Path $session 'journal.json')){$receipt.Journal=Get-Content -LiteralPath (Join-Path $session 'journal.json') -Raw|ConvertFrom-Json}
  try {$receipt.After=Snapshot;$receipt.AfterProbe=Diagnose 'after'} catch {$receipt.Error=([string]$receipt.Error+'; After check: '+$_.Exception.Message)}
  if($receipt.ReachedActive -and $ObservationSeconds -gt 0 -and $receipt.FinalStatus.State -in @('restored','restored-with-warning')) {
    $receipt.Status='observing-after-stop';$receipt.RecoveryObservations=@();Save
    $until=(Get-Date).AddSeconds([Math]::Min($ObservationSeconds,180));$index=0
    do {
      $output=Join-Path (Split-Path $exe) ('.local\diagnostics\recovery-'+$testId+'-'+$index+'.json')
      $observer=Start-Process -FilePath $exe -ArgumentList @('--observe-recovery',('"'+$session+'"'),('"'+$output+'"')) -WindowStyle Hidden -PassThru
      if(!$observer.WaitForExit(8000) -or $observer.ExitCode-ne 0){$receipt.Error=([string]$receipt.Error+'; Recovery observer failed.');break}
      $observation=Get-Content -LiteralPath $output -Raw|ConvertFrom-Json
      $receipt.RecoveryObservations+=$observation
      Save
      $index++
      if((Get-Date)-ge $until){break}
      Start-Sleep -Seconds 5
    }while($true)
    $warnings=@($receipt.RecoveryObservations | ForEach-Object {$_.Warnings} | Select-Object -Unique)
    if($warnings.Count-gt 0){$receipt.Error=([string]$receipt.Error+'; '+($warnings-join '; '))}
    if(($receipt.Before.HidHide|ConvertTo-Json -Compress) -ne ($receipt.After.HidHide|ConvertTo-Json -Compress)){
      $receipt.Error=([string]$receipt.Error+'; HidHide snapshot mismatch.')
    }
    if($receipt.After.Ports -match 'usbip://127.0.0.1:3240/1-1'){
      $receipt.Error=([string]$receipt.Error+'; Virtual port remains.')
    }
  }
  if($receipt.ReachedActive) {
    $backendLog=Get-Content -LiteralPath (Join-Path $session 'backend.log') -Raw
    if($backendLog -notmatch 'shutdown neutral reports written; microphone close written') {
      $receipt.Error=([string]$receipt.Error+'; Final controller neutralization was not confirmed.')
    }
  }
  $receipt.Status=if($receipt.ReachedActive-and !$receipt.Error-and $receipt.FinalStatus.State-eq 'restored'){'passed'}else{'failed'}
  $receipt.FinishedAt=(Get-Date).ToString('o');Save
}
