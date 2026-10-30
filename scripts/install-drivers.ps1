param([switch]$InstallApproved)
$ErrorActionPreference = 'Stop'
if (!$InstallApproved) { throw 'Requires explicit user-approved installation and -InstallApproved.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$logDir = Join-Path $root '.local\logs'
$tempDir = Join-Path $root '.local\installer-temp'
New-Item -ItemType Directory -Force -Path $logDir,$tempDir | Out-Null
$env:TEMP = $tempDir
$env:TMP = $tempDir
$receiptPath = Join-Path $logDir 'driver-install-receipt.json'
$receipt = [ordered]@{
  startedAt = (Get-Date).ToString('o')
  userAuthorization = 'Explicit -InstallApproved flag for this invocation'
  status = 'checking'
  automaticReboot = $false
  rebootRequired = $false
  components = @()
  error = $null
}
function Save-Receipt {
  $json = $receipt | ConvertTo-Json -Depth 8
  [IO.File]::WriteAllText($receiptPath + '.tmp', $json, (New-Object Text.UTF8Encoding($false)))
  Move-Item -LiteralPath ($receiptPath + '.tmp') -Destination $receiptPath -Force
}
function Install-VerifiedPackage($Name, $Version, $Path, $ExpectedHash, $Arguments) {
  $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
  $signature = Get-AuthenticodeSignature -LiteralPath $Path
  if ($hash -ne $ExpectedHash -or $signature.Status -ne 'Valid') {
    throw "Package validation failed: $Name"
  }
  $component = [ordered]@{
    name = $Name; version = $Version; path = $Path
    sha256 = $hash; signature = [string]$signature.Status
    arguments = $Arguments; startedAt = (Get-Date).ToString('o')
    status = 'installing'; exitCode = $null
  }
  $receipt.components += $component
  $receipt.status = 'installing-' + $Name
  Save-Receipt
  $installer = Start-Process -FilePath $Path -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
  $component.exitCode = $installer.ExitCode
  $component.finishedAt = (Get-Date).ToString('o')
  if ($installer.ExitCode -notin @(0,3010)) {
    $component.status = 'failed'
    Save-Receipt
    throw "$Name installer returned $($installer.ExitCode); inspect installer logs."
  }
  $component.status = 'installed'
  if ($installer.ExitCode -eq 3010) { $receipt.rebootRequired = $true }
  Save-Receipt
}
try {
  Save-Receipt
  $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
  if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Windows administrator token required.' }
  if ((Test-Path -LiteralPath 'C:\Program Files\USBip\usbip.exe') -or
      (Test-Path -LiteralPath 'C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe')) {
    throw 'A target dependency is already installed. Review actual state before reinstalling.'
  }
  Install-VerifiedPackage 'usbip-win2' '0.9.8.1' "$root\.local\downloads\USBip-0.9.8.1-x64.exe" '38cad6d4432b52d5bb9409d9ad03b72fdffc4ada4cd3a48fbeca1a2752a8518a' ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010 /SP- /TYPE=compact /TASKS="" /LOG="{0}"' -f (Join-Path $logDir 'usbip-install.log'))
  Install-VerifiedPackage 'HidHide' '1.5.230.0' "$root\.local\downloads\HidHide_1.5.230_x64.exe" 'f4bbbcb82e6258641b887c74bc81c4c5f66e4aa811808dfc304347687b7605f6' ('/exenoui /qn REBOOT=ReallySuppress /L*v "{0}"' -f (Join-Path $logDir 'hidhide-install.log'))
  $receipt.status = 'completed'
  $receipt.finishedAt = (Get-Date).ToString('o')
  Save-Receipt
  exit 0
} catch {
  $receipt.status = 'failed'
  $receipt.error = $_.Exception.Message
  $receipt.finishedAt = (Get-Date).ToString('o')
  Save-Receipt
  exit 1
}
