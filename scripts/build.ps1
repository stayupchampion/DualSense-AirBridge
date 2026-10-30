param([switch]$SkipBackend, [string]$OutputDirectory = 'dist\DualSenseBridge')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$publishRoot=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if (!$publishRoot.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must remain inside the project' }
Set-Location -LiteralPath $root
$env:DOTNET_CLI_HOME=Join-Path $root '.local\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:NUGET_PACKAGES=Join-Path $root '.local\nuget'
$env:NUGET_HTTP_CACHE_PATH=Join-Path $root '.local\nuget-http'
$env:NUGET_PLUGINS_CACHE_PATH=Join-Path $root '.local\nuget-plugins'
$env:TEMP=Join-Path $root '.local\tmp'
$env:TMP=$env:TEMP
New-Item -ItemType Directory -Path $env:TEMP,$env:DOTNET_CLI_HOME -Force | Out-Null
if(-not $SkipBackend) { & (Join-Path $PSScriptRoot 'build-backend.ps1'); if($LASTEXITCODE -ne 0){throw 'Backend build failed'} }
dotnet build src\DsBridge.App\DsBridge.App.csproj -c Release --nologo
if($LASTEXITCODE -ne 0){throw 'App build failed'}
dotnet run --project tests\DsBridge.Tests\DsBridge.Tests.csproj -c Release
if($LASTEXITCODE -ne 0){throw 'Safety tests failed'}
dotnet publish src\DsBridge.App\DsBridge.App.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $publishRoot --nologo
if($LASTEXITCODE -ne 0){throw 'Publish failed'}
$backendRoot=Join-Path $publishRoot 'backend'
New-Item -ItemType Directory -Path $backendRoot -Force | Out-Null
Copy-Item -LiteralPath .local\backend\vdsd.exe -Destination (Join-Path $backendRoot 'vdsd.exe') -Force
if(Test-Path licenses){ Copy-Item -LiteralPath licenses -Destination $publishRoot -Recurse -Force }
if(Test-Path patches){ Copy-Item -LiteralPath patches -Destination $publishRoot -Recurse -Force }
if(Test-Path LICENSE){ Copy-Item -LiteralPath LICENSE -Destination (Join-Path $publishRoot 'LICENSE') -Force }
if(Test-Path docs){ Copy-Item -LiteralPath docs -Destination $publishRoot -Recurse -Force }
if(Test-Path README.md){ Copy-Item -LiteralPath README.md -Destination (Join-Path $publishRoot 'README.md') -Force }
if(Test-Path README.en.md){ Copy-Item -LiteralPath README.en.md -Destination (Join-Path $publishRoot 'README.en.md') -Force }
if(Test-Path assets){ Copy-Item -LiteralPath assets -Destination $publishRoot -Recurse -Force }
Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $publishRoot 'DualSenseBridge.exe'),(Join-Path $backendRoot 'vdsd.exe') | ConvertTo-Json | Set-Content -LiteralPath .local\build-hashes.json -Encoding UTF8
