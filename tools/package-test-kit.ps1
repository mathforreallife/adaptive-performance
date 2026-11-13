param([string]$Output = (Join-Path $PSScriptRoot "..\artifacts\test-kit"))

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$stamp = Get-Date -Format "yyyy-MM-dd"
$folder = Join-Path $Output "GAPO-test-$stamp"
if (Test-Path $folder) { Remove-Item -Recurse -Force $folder -Confirm:$false }
New-Item -ItemType Directory -Force $folder | Out-Null

& $dotnet publish (Join-Path $root "src\Gapo.App\Gapo.App.csproj") -c Release -r win-x64 --self-contained -o $folder -v quiet -nologo
if ($LASTEXITCODE -ne 0) { throw "Publishing the window failed." }
& $dotnet publish (Join-Path $root "src\Gapo.Agent\Gapo.Agent.csproj") -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $folder -v quiet -nologo
if ($LASTEXITCODE -ne 0) { throw "Publishing the agent failed." }
Copy-Item (Join-Path $root "docs\TESTING-ON-A-GAMING-PC.md") (Join-Path $folder "READ-ME-FIRST.md")
Get-ChildItem $folder -Filter "*.pdb" -Recurse | Remove-Item -Force

$zip = "$folder.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $folder "*") -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
"Test kit: $zip ($size MB)"
