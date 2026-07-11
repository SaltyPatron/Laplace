@echo off
setlocal
rem Back-compat wrapper — prefer setup-host.cmd / publish-deploy.cmd.
call "%~dp0env.cmd" || exit /b 1
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0ensure-billing-runtime.ps1" -RepoRoot "%LAPLACE_ROOT%" -RequireService %*
exit /b %ERRORLEVEL%
