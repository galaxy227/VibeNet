param([string]$UnityEditor = 'C:\Program Files\Unity\Hub\Editor\6000.3.20f1\Editor')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $PSScriptRoot 'Artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null
dotnet run --project (Join-Path $PSScriptRoot 'VibeNetTests.csproj') -c Release | Tee-Object (Join-Path $artifacts 'dotnet-tests.txt')
if ($LASTEXITCODE) { throw 'Standalone tests failed' }
$sdkVersion = (dotnet --version).Trim()
$dotnetRoot = Split-Path (Get-Command dotnet).Source -Parent
$csc = Join-Path $dotnetRoot "sdk/$sdkVersion/Roslyn/bincore/csc.dll"
$reference = Join-Path $UnityEditor 'Data/NetStandard/ref/2.1.0/netstandard.dll'
$exe = Join-Path $artifacts 'UnityMonoTests.exe'
dotnet $csc /nologo /noconfig /nostdlib+ /langversion:8 /nullable:enable /target:exe "/out:$exe" "/reference:$reference" (Join-Path $root 'VibeNet.cs') (Join-Path $PSScriptRoot 'Program.cs') (Join-Path $PSScriptRoot 'Load.cs')
if ($LASTEXITCODE) { throw 'Unity reference compilation failed' }
& (Join-Path $UnityEditor 'Data/MonoBleedingEdge/bin/mono.exe') $exe | Tee-Object (Join-Path $artifacts 'unity-mono-tests.txt')
if ($LASTEXITCODE) { throw 'Unity bundled Mono tests failed' }
