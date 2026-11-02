[CmdletBinding()]
param(
  [string]$BuildDirectory = '.local\release\app',
  [string]$OutputDirectory = '.local\release\assets'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function ProjectPath([string]$Relative) {
  $path = [IO.Path]::GetFullPath((Join-Path $root $Relative))
  if (!$path.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Release paths must stay within the project' }
  return $path
}
$build = ProjectPath $BuildDirectory
$output = ProjectPath $OutputDirectory
if ($output.StartsWith($build.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $output -eq $build) { throw 'Assets must be outside the app directory' }
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'src\DsBridge.App\DsBridge.App.csproj') -Raw)).Project.PropertyGroup.Version
# Main-branch CI also packages development builds; only tag builds publish a release.
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') { throw 'Invalid version' }
foreach ($required in @('DualSenseBridge.exe','backend\vdsd.exe','README.md','README.en.md','LICENSE','licenses\vDS-MIT.txt','licenses\Opus-BSD.txt','licenses\dotnet-THIRD-PARTY-NOTICES.txt','assets\branding\dualsense-airbridge.ico')) {
  if (!(Test-Path -LiteralPath (Join-Path $build $required) -PathType Leaf)) { throw "Missing release file: $required" }
}
$files = @(Get-ChildItem -LiteralPath $build -File -Recurse -Force)
foreach ($file in $files) {
  $relative = $file.FullName.Substring($build.Length + 1)
  if ($relative -match '(^|[\\/])(\.local|\.git|bin|obj|__pycache__)([\\/]|$)' -or $file.Extension -in @('.pdb','.log','.zip')) { throw "Unexpected release file: $relative" }
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$name = "DualSense-AirBridge-v$version-win-x64"
$zip = Join-Path $output ($name + '.zip')
if (Test-Path -LiteralPath $zip) { throw 'Refusing to overwrite an existing archive; use a fresh output directory' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($build,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
  if (@($archive.Entries).Count -ne $files.Count) { throw 'ZIP entry count differs from build' }
  if (!($archive.Entries | Where-Object FullName -eq 'DualSenseBridge.exe')) { throw 'ZIP is not directly extractable' }
} finally { $archive.Dispose() }
$commit = (& git -C $root rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0) { $commit = 'uncommitted-local-check' }
$manifest = [ordered]@{
  name = 'DualSense AirBridge'; version = $version; platform = 'win-x64'; sourceCommit = $commit
  backendUpstreamCommit = 'ec531599dfdd02e14ce68efd8c0d2dcb439894a5'
  buildUtc = (Get-Date).ToUniversalTime().ToString('o')
  appSha256 = (Get-FileHash -LiteralPath (Join-Path $build 'DualSenseBridge.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
  backendSha256 = (Get-FileHash -LiteralPath (Join-Path $build 'backend\vdsd.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
  packageSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
  files = $files.Count; bundledDrivers = $false; hardwareTestedByCi = $false
}
$manifestPath = Join-Path $output 'build-manifest.json'
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($manifestPath,($manifest | ConvertTo-Json),$utf8)
$checksums = @($zip,$manifestPath) | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(),[IO.Path]::GetFileName($_) }
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'),(($checksums -join "`n") + "`n"),$utf8)
Write-Output "Packaged $name ($($files.Count) files)"
