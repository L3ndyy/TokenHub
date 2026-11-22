# Removes TokenHub for the current user. Settings and keys in %APPDATA%\TokenHub
# are deliberately kept: they are the user's API keys, not ours to delete.
$ErrorActionPreference = 'SilentlyContinue'

Get-Process -Name TokenHub | Stop-Process -Force

# The optional Windows service needs elevation; without it these lines just fail.
& sc.exe stop TokenHubPool | Out-Null
& sc.exe delete TokenHubPool | Out-Null

Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\TokenHub.lnk') -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'TokenHub.lnk') -Force
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TokenHub' -Recurse -Force

# The running script lives inside the folder it is deleting, so leave last.
Remove-Item $PSScriptRoot -Recurse -Force
