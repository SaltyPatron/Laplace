@echo off
setlocal
call "%~dp0env.cmd" || exit /b 1
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0ensure-billing-runtime.ps1" -RepoRoot "%LAPLACE_ROOT%" %*
exit /b %ERRORLEVEL%
