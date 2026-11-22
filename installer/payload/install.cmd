@echo off
rem TokenHub setup: the real work happens in install.ps1 (PowerShell handles the
rem Cyrillic text and the shortcut shell objects). This launcher stays ASCII-only.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
