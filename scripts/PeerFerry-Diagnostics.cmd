@echo off
title PeerFerry diagnostics
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0collect-diagnostics.ps1"
echo.
pause
