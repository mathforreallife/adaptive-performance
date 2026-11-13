param([string]$Output = (Join-Path $PSScriptRoot "..\artifacts\release"))

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
$folder = Join-Path $Output "GAPO-$version"
if (Test-Path $folder) { Remove-Item -Recurse -Force $folder -Confirm:$false }
New-Item -ItemType Directory -Force $folder | Out-Null

& $dotnet publish (Join-Path $root "src\Gapo.App\Gapo.App.csproj") -c Release -r win-x64 --self-contained -o $folder -v quiet -nologo
if ($LASTEXITCODE -ne 0) { throw "Publishing the window failed." }
& $dotnet publish (Join-Path $root "src\Gapo.Agent\Gapo.Agent.csproj") -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $folder -v quiet -nologo
if ($LASTEXITCODE -ne 0) { throw "Publishing the agent failed." }
Get-ChildItem $folder -Filter "*.pdb" -Recurse | Remove-Item -Force
Copy-Item (Join-Path $root "tools\release\Install-GAPO.ps1") $folder
Copy-Item (Join-Path $root "tools\release\Uninstall-GAPO.ps1") $folder
Copy-Item (Join-Path $root "tools\release\Install.cmd") $folder
Copy-Item (Join-Path $root "docs\USER-GUIDE.md") (Join-Path $folder "READ-ME-FIRST.md")

$zip = Join-Path $Output "GAPO-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $folder "*") -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content -Path "$zip.sha256" -Value "$hash  GAPO-$version-win-x64.zip" -Encoding ascii
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
"Release: $zip ($size MB), SHA-256 $hash"
