[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Exe = Join-Path $ProjectRoot '.local\backend\vdsd.exe'
$TestRoot = Join-Path $ProjectRoot ('.local\tests\backend-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $TestRoot | Out-Null
function Start-Backend {
  param([string[]]$BackendArgs,[string]$UsbipOverride = '')
  $Info = New-Object Diagnostics.ProcessStartInfo
  $Info.FileName = $Exe
  $Info.Arguments = (($BackendArgs | ForEach-Object { '"' + $_ + '"' }) -join ' ')
  $Info.WorkingDirectory = $TestRoot
  $Info.UseShellExecute = $false
  $Info.CreateNoWindow = $true
  $Info.RedirectStandardOutput = $true
  $Info.RedirectStandardError = $true
  if ($UsbipOverride) { $Info.EnvironmentVariables['VDS_USBIP_EXE'] = $UsbipOverride }
  return [Diagnostics.Process]::Start($Info)
}
function Finish-Backend {
  param([Diagnostics.Process]$Process)
  if (!$Process.WaitForExit(10000)) { $Process.Kill(); throw 'Backend did not exit within 10 seconds' }
  $Result = [ordered]@{exitCode=$Process.ExitCode;stdout=$Process.StandardOutput.ReadToEnd();stderr=$Process.StandardError.ReadToEnd()}
  $Process.Dispose()
  return $Result
}
$Results = [ordered]@{}
function Invoke-Control {
  param([string]$Command)
  $Client=[IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::InOut)
  try {
    $Client.Connect(5000)
    $Bytes=[Text.Encoding]::UTF8.GetBytes($Command+"`n")
    $Client.Write($Bytes,0,$Bytes.Length)
    $Client.Flush()
    $Buffer=New-Object byte[] 8192
    $Read=$Client.ReadAsync($Buffer,0,$Buffer.Length)
    if(!$Read.Wait(5000)){throw 'Control timed out'}
    return ([Text.Encoding]::UTF8.GetString($Buffer,0,$Read.Result)|ConvertFrom-Json)
  }finally{$Client.Dispose()}
}
$Results.identity = Finish-Backend (Start-Backend @('--bridge-info'))
if ($Results.identity.exitCode -ne 0 -or ($Results.identity.stdout | ConvertFrom-Json).bridgeProtocol -ne 1) { throw 'Identity check failed' }
$Results.missingArguments = Finish-Backend (Start-Backend @())
if ($Results.missingArguments.exitCode -ne 1 -or $Results.missingArguments.stderr -notmatch 'requires explicit') { throw 'Missing argument refusal failed' }
$PipeName = 'DSBridge-test-' + [Guid]::NewGuid().ToString('N')
$EventName = 'Local\DSBridge-test-' + [Guid]::NewGuid().ToString('N')
$DbPath = Join-Path $TestRoot 'empty.db'
[IO.File]::WriteAllText($DbPath,'',[Text.UTF8Encoding]::new($false))
$SessionArgs = @('--db-path',$DbPath,'--log',(Join-Path $TestRoot 'backend.log'),'--pipe',('\\.\pipe\'+$PipeName),'--stop-event',$EventName)
$Results.missingEvent = Finish-Backend (Start-Backend $SessionArgs)
if ($Results.missingEvent.exitCode -ne 1 -or $Results.missingEvent.stderr -notmatch 'stop event does not exist') { throw 'Missing event refusal failed' }
$StopEvent = [Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$EventName)
try {
  $Results.missingDriver = Finish-Backend (Start-Backend $SessionArgs (Join-Path $TestRoot 'no-usbip.exe'))
  if ($Results.missingDriver.exitCode -ne 1 -or $Results.missingDriver.stderr -notmatch 'usbip-win2 is not installed') { throw 'Missing driver refusal failed' }
  # Stub ONLY provider discovery. The empty DB guarantees no controller bridge,
  # USB/IP command, virtual port, HID write or HidHide mutation is requested.
  $Process = Start-Backend $SessionArgs $Exe
  $Pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::InOut)
  try {
    $Pipe.Connect(5000)
    $Pipe.ReadMode = [IO.Pipes.PipeTransmissionMode]::Message
    $Bytes = [Text.Encoding]::UTF8.GetBytes('{"command":"trace","mode":"off","scope":"all"}' + "`n")
    $Pipe.Write($Bytes,0,$Bytes.Length)
    $Pipe.Flush()
    $Buffer = New-Object byte[] 8192
    $ReadTask = $Pipe.ReadAsync($Buffer,0,$Buffer.Length)
    if (!$ReadTask.Wait(5000)) { throw 'Control pipe reply timed out' }
    $Reply = [Text.Encoding]::UTF8.GetString($Buffer,0,$ReadTask.Result)
    $Results.privatePipe = $Reply | ConvertFrom-Json
    if (!$Results.privatePipe.OK) { throw ('Private pipe command failed: read=' + $ReadTask.Result + ' reply=' + $Reply) }
    $Pipe.Dispose()
    $Results.defaultPreferences=Invoke-Control '{"command":"bridge-status"}'
    if(!$Results.defaultPreferences.OK -or !$Results.defaultPreferences.speakerMuted -or $Results.defaultPreferences.connected -or $Results.defaultPreferences.batteryRaw -ne -1){throw 'Invalid initial telemetry'}
    $Results.unmute=Invoke-Control '{"command":"speaker","mode":"unmute"}'
    if(!$Results.unmute.OK -or $Results.unmute.speakerMuted){throw 'Unmute not acknowledged'}
    $Results.invalidMode=Invoke-Control '{"command":"speaker","mode":"invalid"}'
    if($Results.invalidMode.OK){throw 'Invalid preference accepted'}
    $Results.extraField=Invoke-Control '{"command":"speaker","mode":"mute","unexpected":"x"}'
    if($Results.extraField.OK){throw 'Unexpected preference field accepted'}
    $Results.afterRejected=Invoke-Control '{"command":"bridge-status"}'
    if($Results.afterRejected.speakerMuted){throw 'Rejected request altered preference'}
    $Results.mute=Invoke-Control '{"command":"speaker","mode":"mute"}'
    if(!$Results.mute.OK -or !$Results.mute.speakerMuted){throw 'Mute not acknowledged'}
  } finally {
    $Pipe.Dispose()
    [void]$StopEvent.Set()
    $Results.stopEvent = Finish-Backend $Process
  }
  if ($Results.stopEvent.exitCode -ne 0) { throw 'Event-driven graceful stop failed' }
} finally {
  $StopEvent.Dispose()
}
$Results.notes = 'Software-only tests. No driver install, no physical HID output, no virtual controller attached. Provider discovery was stubbed only for empty-session stop test.'
$Results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $TestRoot 'results.json') -Encoding UTF8
$Results | ConvertTo-Json -Depth 8
Write-Output "Saved: $TestRoot"
