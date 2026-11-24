# TokenHub setup. Installs the app for the current user only: no administrator
# rights, no registry changes outside HKCU. The Windows service (optional) is
# installed later from inside the app, where the UAC prompt is expected.
$ErrorActionPreference = 'Stop'

$source = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\TokenHub'
$appExe = Join-Path $target 'TokenHub.exe'
$poolExe = Join-Path $target 'poolservice\TokenHub.PoolService.exe'

Write-Host 'TokenHub: установка...'

# A running copy holds the files open, so stop it before replacing them.
Get-Process -Name TokenHub -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force -Path $target | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $poolExe) | Out-Null

Copy-Item (Join-Path $source 'TokenHub.exe') $appExe -Force

$shippedService = Join-Path $source 'TokenHub.PoolService.exe'
if (Test-Path $shippedService) {
    Copy-Item $shippedService $poolExe -Force
}

Copy-Item (Join-Path $source 'uninstall.ps1') (Join-Path $target 'uninstall.ps1') -Force

$version = (Get-Item $appExe).VersionInfo.ProductVersion
if ($version) { $version = $version.Split('+')[0] } else { $version = '1.0.0' }

# Shortcuts: Start menu and desktop.
$shell = New-Object -ComObject WScript.Shell
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
foreach ($folder in @($startMenu, [Environment]::GetFolderPath('Desktop'))) {
    try {
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
        $shortcut = $shell.CreateShortcut((Join-Path $folder 'TokenHub.lnk'))
        $shortcut.TargetPath = $appExe
        $shortcut.WorkingDirectory = $target
        $shortcut.IconLocation = "$appExe,0"
        $shortcut.Description = 'TokenHub — пул ключей Token Harbor для OpenCode'
        $shortcut.Save()
    }
    catch {
        Write-Host "Не удалось создать ярлык в $folder"
    }
}

# "Apps and features" entry, current user only.
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TokenHub'
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty $uninstallKey DisplayName 'TokenHub'
Set-ItemProperty $uninstallKey DisplayVersion $version
Set-ItemProperty $uninstallKey Publisher 'L3ndyy'
Set-ItemProperty $uninstallKey InstallLocation $target
Set-ItemProperty $uninstallKey DisplayIcon "$appExe,0"
Set-ItemProperty $uninstallKey NoModify 1 -Type DWord
Set-ItemProperty $uninstallKey NoRepair 1 -Type DWord
Set-ItemProperty $uninstallKey UninstallString `
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $target 'uninstall.ps1')`""

# Start with Windows, minimised to the tray: the pool should be ready before the
# first OpenCode prompt of the day. The app shows this switch as already on.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $runKey -Force | Out-Null
Set-ItemProperty $runKey TokenHub "`"$appExe`" --minimized"

Write-Host "TokenHub $version установлен в $target"
Write-Host 'Ключи и настройки лежат в %APPDATA%\TokenHub и при удалении сохраняются.'
Write-Host ''

# Ask instead of always launching, so a scripted install stays quiet.
if ([Environment]::UserInteractive) {
    $answer = Read-Host 'Запустить TokenHub сейчас? (Y/n)'
    if ($answer -eq '' -or $answer -match '^(y|yes|д|да)$') {
        Start-Process $appExe
    }
}
