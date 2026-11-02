[CmdletBinding()]
param(
  [string]$CMake = 'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe',
  [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$LocalRoot = Join-Path $ProjectRoot '.local'
$DownloadRoot = Join-Path $LocalRoot 'downloads'
$UpstreamRoot = Join-Path $LocalRoot 'upstream'
$TempRoot = Join-Path $LocalRoot 'tmp'
$OpusPrefix = Join-Path $LocalRoot 'opus'
$OutputRoot = Join-Path $LocalRoot 'backend'
foreach ($Dir in @($DownloadRoot,$UpstreamRoot,$TempRoot,$OutputRoot)) { New-Item -ItemType Directory -Force -Path $Dir | Out-Null }
$env:TEMP = $TempRoot
$env:TMP = $TempRoot
$env:PYTHONDONTWRITEBYTECODE = '1'
function Invoke-Checked {
  param([string]$Exe, [string[]]$Arguments)
  & $Exe @Arguments
  if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $Exe $($Arguments -join ' ')" }
}
function Get-PinnedArchive {
  param([string]$Url,[string]$Name,[string]$Sha256,[string]$Directory)
  $Archive = Join-Path $DownloadRoot $Name
  if (!(Test-Path -LiteralPath $Archive)) { Invoke-WebRequest -Uri $Url -OutFile $Archive -UseBasicParsing }
  $Actual = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash
  if ($Actual -ne $Sha256) { throw "Pinned archive checksum mismatch: $Archive" }
  if (!(Test-Path -LiteralPath (Join-Path $UpstreamRoot $Directory))) { Expand-Archive -LiteralPath $Archive -DestinationPath $UpstreamRoot }
}
Get-PinnedArchive 'https://codeload.github.com/hurryman2212/vds/zip/ec531599dfdd02e14ce68efd8c0d2dcb439894a5' 'vds-ec531599.zip' '6AEAE1A84FA8CE7617757CF203CBB234AA3227373EF896A49BBD61697DEC93E5' 'vds-ec531599dfdd02e14ce68efd8c0d2dcb439894a5'
Get-PinnedArchive 'https://codeload.github.com/xiph/opus/zip/refs/tags/v1.5.2' 'opus-v1.5.2.zip' 'C26F6200778C844CF5211F0D2DBAAFADCE3F20E9E8EFE7495E01AAA9987C5B13' 'opus-1.5.2'
Invoke-Checked $Python @((Join-Path $PSScriptRoot 'patch-backend.py'),'--root',$ProjectRoot)
Invoke-Checked $Python @((Join-Path $ProjectRoot 'tests\test_diagnostic_patch.py'))
$OpusSource = Join-Path $UpstreamRoot 'opus-1.5.2'
$OpusBuild = Join-Path $LocalRoot 'build\opus'
$VdsSource = Join-Path $LocalRoot 'buildsrc\vds'
$VdsBuild = Join-Path $LocalRoot 'build\vds'
Invoke-Checked $CMake @('-S',$OpusSource,'-B',$OpusBuild,'-G','Visual Studio 17 2022','-A','x64','-DOPUS_BUILD_SHARED_LIBRARY=OFF','-DOPUS_STATIC_RUNTIME=ON','-DOPUS_BUILD_TESTING=OFF','-DOPUS_BUILD_PROGRAMS=OFF',"-DCMAKE_INSTALL_PREFIX=$OpusPrefix")
Invoke-Checked $CMake @('--build',$OpusBuild,'--config','Release','--parallel','4')
# This installs a library ONLY into .local/opus; no driver, service, PATH, or registry installation.
Invoke-Checked $CMake @('--install',$OpusBuild,'--config','Release','--prefix',$OpusPrefix)
Invoke-Checked $CMake @('-S',$VdsSource,'-B',$VdsBuild,'-G','Visual Studio 17 2022','-A','x64',"-DCMAKE_PREFIX_PATH=$OpusPrefix",'-DINSTALL_SERVICE=OFF','-DSTOP_SERVICE=OFF','-DCMAKE_POLICY_DEFAULT_CMP0091=NEW','-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded',"-DCMAKE_INSTALL_PREFIX=$OutputRoot")
Invoke-Checked $CMake @('--build',$VdsBuild,'--config','Release','--target','vdsd','dsbridge_runtime_tests','--parallel','4')
Invoke-Checked (Join-Path $VdsBuild 'Release\dsbridge_runtime_tests.exe') @()
# Deliberately never run cmake --install for vDS.
Copy-Item -LiteralPath (Join-Path $VdsBuild 'Release\vdsd.exe') -Destination (Join-Path $OutputRoot 'vdsd.exe') -Force
Copy-Item -LiteralPath (Join-Path $OpusSource 'COPYING') -Destination (Join-Path $ProjectRoot 'licenses\Opus-BSD.txt') -Force
Copy-Item -LiteralPath (Join-Path $OpusSource 'LICENSE_PLEASE_READ.txt') -Destination (Join-Path $ProjectRoot 'licenses\Opus-LICENSE_PLEASE_READ.txt') -Force
$InfoText = & (Join-Path $OutputRoot 'vdsd.exe') --bridge-info
if ($LASTEXITCODE -ne 0) { throw 'Backend identity check failed' }
$Info = $InfoText | ConvertFrom-Json
if ($Info.bridgeProtocol -ne 1 -or !$Info.managedHidHide -or !$Info.stopEvent -or !$Info.exclusiveUsbip -or $Info.serviceMode -or !$Info.singleConnectionSession -or !$Info.controllerPreferences -or !$Info.independentAudioControls) { throw 'Backend is not the managed foreground fork' }
$Manifest = [ordered]@{
  bridgeProtocol = 1
  upstreamCommit = 'ec531599dfdd02e14ce68efd8c0d2dcb439894a5'
  opusVersion = '1.5.2'
  opusCommit = 'ddbe48383984d56acd9e1ab6a090c54ca6b735a6'
  opusLinkage = 'static'
  msvcRuntime = 'static'
  executableSha256 = (Get-FileHash -LiteralPath (Join-Path $OutputRoot 'vdsd.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
  serviceInstalled = $false
  driversInstalled = $false
  singleConnectionSession = $true
  controllerPreferences = $true
  independentAudioControls = $true
}
$Manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'backend-manifest.json') -Encoding UTF8
Write-Output $InfoText
Write-Output "Backend ready: $OutputRoot"
