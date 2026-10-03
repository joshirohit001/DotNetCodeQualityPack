@echo off
rem Double-click to build the .vsix and the installer (Setup.exe + zip) into the dist folder.
rem Needs Visual Studio 2022 with the "Visual Studio extension development" workload; Setup.exe also needs Inno Setup 6.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Installers.ps1"
echo.
pause
