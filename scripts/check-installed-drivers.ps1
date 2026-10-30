# Read-only system inspection; reports are written only under the project .local directory.
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$logDir=Join-Path $root '.local\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$task=Get-ScheduledTask -TaskName 'nefarius_HidHide_Updater' -ErrorAction SilentlyContinue
$updater=[ordered]@{TaskFound=($null -ne $task);TaskState=if($task){[string]$task.State}else{'NotInstalled'};RunningCount=@(Get-Process -Name nefarius_HidHide_Updater -ErrorAction SilentlyContinue).Count}
$drivers=@(Get-CimInstance Win32_SystemDriver | Where-Object { $_.Name -in @('HidHide','usbip2_ude','usbip2_filter') } | Select-Object Name,State,StartMode,PathName)
$devices=@(Get-PnpDevice -PresentOnly | Where-Object { $_.FriendlyName -match 'USB/IP|USBip' -or $_.InstanceId -like 'HID\VID_054C&PID_0CE6*' } | ForEach-Object {
  $code=Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_ProblemCode' -ErrorAction SilentlyContinue
  [pscustomobject]@{Status=$_.Status;FriendlyName=$_.FriendlyName;InstanceId=$_.InstanceId;ProblemCode=$code.Data}
})
$tasks=@(Get-ScheduledTask | Where-Object { $_.TaskName -match 'USBip|DualSenseBridge|vdsd|HidHide' } | ForEach-Object {
  $t=$_
  $xml=Export-ScheduledTask -TaskName $t.TaskName -TaskPath $t.TaskPath
  $safeName=$t.TaskName -replace '[^a-zA-Z0-9_-]','_'
  [IO.File]::WriteAllText((Join-Path $logDir ($safeName+'.xml')),$xml,(New-Object Text.UTF8Encoding($false)))
  [pscustomobject]@{Name=$t.TaskName;Path=$t.TaskPath;State=[string]$t.State;Actions=@($t.Actions | Select-Object Execute,Arguments);TriggerTypes=@($t.Triggers | ForEach-Object {$_.CimClass.CimClassName})}
})
$hide='C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe'
$hideState=@('--cloak-state','--inv-state','--dev-list','--app-list') | ForEach-Object {
  if (!(Test-Path -LiteralPath $hide)) { return [pscustomobject]@{Argument=$_;ExitCode=$null;Output='Not installed'} }
  $out=& $hide $_ 2>&1
  [pscustomobject]@{Argument=$_;ExitCode=$LASTEXITCODE;Output=($out -join [Environment]::NewLine)}
}
$usbip='C:\Program Files\USBip\usbip.exe'
$portExit=$null
$ports=@('Not installed')
if(Test-Path -LiteralPath $usbip) { $ports=& $usbip port 2>&1; $portExit=$LASTEXITCODE }
$uninstallRoots=@('HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')
$installed=@($uninstallRoots | ForEach-Object {Get-ItemProperty -Path ($_+'\*') -ErrorAction SilentlyContinue} | Where-Object {$_.DisplayName -match 'USBip|HidHide'} | Select-Object DisplayName,DisplayVersion,InstallLocation)
$runEntries=@()
foreach ($key in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run')) {
  if (Test-Path $key) {
    $r=Get-Item -LiteralPath $key
    foreach ($name in $r.GetValueNames()) {
      $value=[string]$r.GetValue($name)
      if ($name -match 'DualSenseBridge|HidHide|usbip|vdsd' -or $value -match 'DualSenseBridge|HidHide|usbip|vdsd') {
        $runEntries += [pscustomobject]@{Key=$key;Name=$name;Value=$value}
      }
    }
  }
}
$result=[ordered]@{
  CheckedAt=(Get-Date).ToString('o');Updater=$updater;InstalledProducts=$installed
  Drivers=$drivers;Devices=$devices;Tasks=$tasks;HidHide=$hideState
  UsbipPortExitCode=$portExit;UsbipPorts=($ports -join [Environment]::NewLine)
  VdsServiceExists=(Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\vdsd')
  RelevantRunEntries=$runEntries
  BridgeProcesses=@(Get-Process -Name vdsd,DualSenseBridge -ErrorAction SilentlyContinue | Select-Object Name,Id)
  LastBootUpTime=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('o')
}
[IO.File]::WriteAllText((Join-Path $logDir 'driver-postinstall-check.json'),($result|ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
