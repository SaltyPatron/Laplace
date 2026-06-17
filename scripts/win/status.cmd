@echo off
setlocal
call "%~dp0env.cmd"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0status.ps1"
exit /b %ERRORLEVEL%
