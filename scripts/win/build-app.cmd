@echo off
rem build-app.cmd — Release build of app/Laplace.slnx (no publish / IIS).
setlocal
call "%~dp0env.cmd"
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
cd /d "%LAPLACE_ROOT%"
exit /b 0
