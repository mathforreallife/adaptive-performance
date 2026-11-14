param(
    [switch]$RevertGameSettings,
    [switch]$KeepGameSettings,
    [switch]$RemoveData,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$target = Join-Path $env:LOCALAPPDATA "Programs\GAPO"
$data = Join-Path $env:LOCALAPPDATA "GAPO"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GAPO"
$agent = Join-Path $target "Gapo.Agent.exe"
Set-Location $env:TEMP

$running = Get-Process -Name "GAPO", "Gapo.Agent" -ErrorAction SilentlyContinue
if ($running) {
    throw "GAPO is running. Close the GAPO window, then uninstall again."
}

$revert = $RevertGameSettings
if (-not $revert -and -not $KeepGameSettings -and -not $Quiet -and (Test-Path $agent)) {
    $answer = Read-Host "Put every game setting GAPO changed back to its original value before uninstalling? (Y/N)"
    $revert = $answer -match "^[Yy]"
}

if ($revert -and (Test-Path $agent)) {
    & $agent --revert-all
}

foreach ($place in @([Environment]::GetFolderPath("Programs"), [Environment]::GetFolderPath("Desktop"))) {
    $link = Join-Path $place "GAPO.lnk"
    if (Test-Path $link) { Remove-Item $link -Force }
}

if (Test-Path $uninstallKey) { Remove-Item $uninstallKey -Recurse -Force }
if (Test-Path $target) { Remove-Item $target -Recurse -Force -Confirm:$false }
if ($RemoveData -and (Test-Path $data)) { Remove-Item $data -Recurse -Force -Confirm:$false }

Write-Host "GAPO was removed."
if (-not $RemoveData -and (Test-Path $data)) {
    Write-Host "Your reports, preferences and the record of original game settings were kept in $data."
}
Write-Host "If the installer added your account to 'Performance Log Users', you can remove it there (Computer Management > Local Users and Groups)."
