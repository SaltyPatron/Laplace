@echo off
setlocal
call "%~dp0env.cmd"
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0ensure-managed-services.ps1" -RepoRoot "%LAPLACE_ROOT%" %*
exit /b %ERRORLEVEL%
