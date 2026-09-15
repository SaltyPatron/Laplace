@echo off
setlocal
call "%~dp0env.cmd"
pwsh -NoProfile -File "%~dp0build-cutechess.ps1"
exit /b %ERRORLEVEL%
