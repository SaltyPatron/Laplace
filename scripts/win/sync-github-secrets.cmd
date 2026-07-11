@echo off
setlocal
call "%~dp0env.cmd" || exit /b 1
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0sync-github-secrets.ps1" -RepoRoot "%LAPLACE_ROOT%" %*
exit /b %ERRORLEVEL%
