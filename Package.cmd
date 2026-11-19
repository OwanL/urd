@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\portable.ps1" -Action Package
set "result=%errorlevel%"
if not "%result%"=="0" echo Package failed. See the message above.
pause
exit /b %result%
