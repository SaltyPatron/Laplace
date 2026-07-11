@echo off
setlocal
rem =============================================================================
rem Windows operator entry: local secrets + Stripe listen + push to GitHub Secrets.
rem
rem   scripts\win\setup-host.cmd          (elevated for NSSM Stripe listen)
rem
rem Deploy path is GitHub Secrets → CI publish → /opt/laplace/secrets on hart-server.
rem Local .env is only the source you push FROM — not what the server reads.
rem =============================================================================
set "HERE=%~dp0"
call "%HERE%env.cmd" || exit /b 1

net session >nul 2>&1
if errorlevel 1 (
  echo [setup-host] ERROR: run elevated ^(Administrator^) once:
  echo   scripts\win\setup-host.cmd
  exit /b 2
)

echo ==== [setup-host] local deploy/secrets + Stripe listen ====
pwsh -NoProfile -ExecutionPolicy Bypass -File "%HERE%ensure-billing-runtime.ps1" ^
  -RepoRoot "%LAPLACE_ROOT%" -RequireService
if errorlevel 1 exit /b 1

echo ==== [setup-host] push .env → GitHub repository Secrets ====
pwsh -NoProfile -ExecutionPolicy Bypass -File "%HERE%sync-github-secrets.ps1" ^
  -RepoRoot "%LAPLACE_ROOT%"
if errorlevel 1 exit /b 1

echo ==== [setup-host] publish + deploy API ^(IIS^) ====
call "%HERE%publish-deploy.cmd"
exit /b %ERRORLEVEL%
