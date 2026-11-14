param(
    [switch]$GrantMeasuringPermission,
    [switch]$SkipMeasuringPermission,
    [switch]$NoDesktopShortcut,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$source = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA "Programs\GAPO"
$performanceLogUsers = "S-1-5-32-559"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GAPO"

if (-not (Test-Path (Join-Path $source "GAPO.exe")) -or -not (Test-Path (Join-Path $source "Gapo.Agent.exe"))) {
    throw "Run this script from the unzipped GAPO folder; GAPO.exe and Gapo.Agent.exe must be next to it."
}

$version = ((Get-Item (Join-Path $source "GAPO.exe")).VersionInfo.ProductVersion -split "\+")[0]

$running = Get-Process -Name "GAPO", "Gapo.Agent" -ErrorAction SilentlyContinue
if ($running) {
    throw "GAPO is running. Close the GAPO window, then run the installer again."
}

Write-Host "Installing GAPO $version to $target"
if (Test-Path $target) {
    Get-ChildItem $target | Remove-Item -Recurse -Force -Confirm:$false
}
New-Item -ItemType Directory -Force $target | Out-Null
Get-ChildItem $source | Copy-Item -Destination $target -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$places = @([Environment]::GetFolderPath("Programs"))
if (-not $NoDesktopShortcut) { $places += [Environment]::GetFolderPath("Desktop") }
foreach ($place in $places) {
    $link = $shell.CreateShortcut((Join-Path $place "GAPO.lnk"))
    $link.TargetPath = Join-Path $target "GAPO.exe"
    $link.WorkingDirectory = $target
    $link.Description = "GAPO: measures your games and optimises their settings"
    $link.Save()
}

New-Item -Path $uninstallKey -Force | Out-Null
$uninstaller = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $target 'Uninstall-GAPO.ps1')`""
$entries = @{
    DisplayName = "GAPO"
    DisplayVersion = $version
    Publisher = "Adaptive Performance"
    InstallLocation = $target
    DisplayIcon = Join-Path $target "GAPO.exe"
    UninstallString = $uninstaller
}
foreach ($name in $entries.Keys) {
    Set-ItemProperty -Path $uninstallKey -Name $name -Value $entries[$name]
}
Set-ItemProperty -Path $uninstallKey -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name "NoRepair" -Value 1 -Type DWord
$size = [int]((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
Set-ItemProperty -Path $uninstallKey -Name "EstimatedSize" -Value $size -Type DWord

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$alreadyAllowed = $identity.Groups | Where-Object { $_.Value -eq $performanceLogUsers -or $_.Value -eq "S-1-5-32-544" }
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$signOutNeeded = $false
if (-not $alreadyAllowed -and -not $elevated -and -not $SkipMeasuringPermission) {
    Write-Host ""
    Write-Host "GAPO reads frame times from Windows event tracing. Windows allows that to administrators and to members"
    Write-Host "of the built-in 'Performance Log Users' group. Adding your account to that group once lets GAPO measure"
    Write-Host "games without running as administrator. Windows asks for permission, and you sign out and in once afterwards."
    $grant = $GrantMeasuringPermission
    if (-not $grant -and -not $Quiet) {
        $answer = Read-Host "Add your account to Performance Log Users now? (Y/N)"
        $grant = $answer -match "^[Yy]"
    }

    if ($grant) {
        $command = "Add-LocalGroupMember -SID '$performanceLogUsers' -Member '$($identity.User.Value)' -ErrorAction Stop"
        try {
            $process = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList "-NoProfile", "-Command", $command
            if ($process.ExitCode -eq 0) {
                $signOutNeeded = $true
            }
            else {
                Write-Warning "Your account could not be added to Performance Log Users. GAPO still shows processor use; run it as administrator to see frame times."
            }
        }
        catch {
            Write-Warning "Permission was not given. GAPO still shows processor use; run it as administrator to see frame times."
        }
    }
}

Write-Host ""
Write-Host "GAPO is installed. Start it from the Start menu (GAPO)."
if ($signOutNeeded) {
    Write-Host "Sign out of Windows and back in once, so the new permission takes effect."
}
Write-Host "To remove it: Settings > Apps > Installed apps > GAPO > Uninstall."
