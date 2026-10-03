@echo off
rem Double-click to remove the .NET Code Quality Pack from Visual Studio 2022.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1"
echo.
pause
