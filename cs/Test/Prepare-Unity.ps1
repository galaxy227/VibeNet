param([string]$Destination = (Join-Path $PSScriptRoot '../../work/UnityQualification'))
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force "$target/Assets/Editor", "$target/Packages", "$target/ProjectSettings" | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../VibeNet.cs') -Destination "$target/Assets/VibeNet.cs"
foreach ($file in @('Program.cs', 'Load.cs')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination "$target/Assets/$file" }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Unity/VibeNetQualification.cs') -Destination "$target/Assets/VibeNetQualification.cs"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Unity/Build.cs') -Destination "$target/Assets/Editor/Build.cs"
'{"dependencies":{}}' | Set-Content "$target/Packages/manifest.json"
'm_EditorVersion: 6000.3.20f1' | Set-Content "$target/ProjectSettings/ProjectVersion.txt"
Write-Output "Prepared $target. Open with a licensed Unity editor and run VibeNetQualificationBuild.Build, then launch Build/VibeNetQualification.exe."
