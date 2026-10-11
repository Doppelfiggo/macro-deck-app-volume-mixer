@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" > "%~dp0build.log" 2>&1
echo EXITCODE=%ERRORLEVEL% >> "%~dp0build.log"
