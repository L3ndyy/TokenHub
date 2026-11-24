# Removes TokenHub for the current user. Settings and keys in %APPDATA%\TokenHub
# are the user's API keys, not ours to delete, so they stay unless explicitly asked
# for at the end.
$ErrorActionPreference = 'SilentlyContinue'

Get-Process -Name TokenHub | Stop-Process -Force

# The optional Windows service needs elevation; without it these lines just fail.
& sc.exe stop TokenHubPool | Out-Null
& sc.exe delete TokenHubPool | Out-Null

Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\TokenHub.lnk') -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'TokenHub.lnk') -Force
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TokenHub' -Recurse -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name TokenHub -ErrorAction SilentlyContinue

Write-Host 'TokenHub удалён. Настройки и ключи в %APPDATA%\TokenHub сохранены.'

# Deleting keys is irreversible, so it is an explicit yes, never the default.
if ([Environment]::UserInteractive) {
    Write-Host ''
    $answer = Read-Host 'Удалить также настройки и ключи из %APPDATA%\TokenHub? (y/N)'
    if ($answer -match '^(y|yes|д|да)$') {
        $settings = Join-Path $env:APPDATA 'TokenHub'
        Write-Host "Удаляю $settings"
        Remove-Item $settings -Recurse -Force
        Write-Host 'Настройки и ключи удалены.'
    }
    else {
        Write-Host 'Настройки оставлены — их можно удалить вручную.'
    }
}

# The running script lives inside the folder it is deleting, so leave last.
Remove-Item $PSScriptRoot -Recurse -Force