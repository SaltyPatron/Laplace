@echo off
setlocal
rem One-shot state of the world: tree freshness, deploy currency, PG, endpoint, locks.
rem Run this FIRST -- it tells you what is already current so you only rebuild what changed.
call "%~dp0env.cmd"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0status.ps1"
exit /b %ERRORLEVEL%
