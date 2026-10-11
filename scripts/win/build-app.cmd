@echo off
rem Build the managed solution and its runnable CLI. IIS deployment is separate.
setlocal
call "%~dp0env.cmd" || exit /b 1
cd /d "%LAPLACE_ROOT%\app" || exit /b 1

set "SKIP_CLEAN=0"
if /i "%~1"=="--skip-clean" set "SKIP_CLEAN=1"

if "%SKIP_CLEAN%"=="0" (
  echo build-app: dotnet clean Release ...
  dotnet clean Laplace.slnx -c Release --nologo -v minimal || exit /b 1
) else (
  echo build-app: dotnet clean skipped
)

echo build-app: dotnet build Release ...
dotnet build Laplace.slnx -c Release -v minimal || exit /b 1

pwsh -NoProfile -File "%~dp0publish-cli.ps1" || exit /b 1

cd /d "%LAPLACE_ROOT%"
exit /b 0
