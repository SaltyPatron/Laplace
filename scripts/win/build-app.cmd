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

rem The ingest CLI runs from a SEPARATE ReadyToRun publish tree (net10.0-r2r) that
rem seed-step.cmd gates on EXISTENCE, not freshness — so a rebuild here would leave
rem seed-step executing the previous exe forever (measured: a full rebuild-all still
rem ran pre-fix ingest code). Invalidate the r2r stamp so the next seed-step
rem republishes the ReadyToRun tree from these fresh assemblies.
set "CLI_R2R_STAMP=%LAPLACE_BUILD_ROOT%\app\bin\Laplace.Cli\Release\net10.0-r2r\Laplace.Cli.exe.r2r-stamp"
if exist "%CLI_R2R_STAMP%" (
  del /q "%CLI_R2R_STAMP%"
  echo build-app: invalidated CLI ReadyToRun stamp — seed-step will republish
)

cd /d "%LAPLACE_ROOT%"
exit /b 0
