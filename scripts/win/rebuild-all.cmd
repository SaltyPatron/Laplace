@echo off
setlocal EnableDelayedExpansion

set "SKIP_CLEAN=0"
set "SKIP_APP=0"

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--skip-clean" ( set "SKIP_CLEAN=1"  & shift /1 & goto parse_args )
if /i "%~1"=="--skip-app"   ( set "SKIP_APP=1"    & shift /1 & goto parse_args )
echo unknown flag: %~1
exit /b 2

:args_done
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

if "%SKIP_CLEAN%"=="0" (
  echo.
  echo ===== PHASE 1 — CLEAN =====
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-win || exit /b 1
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-win-ext || exit /b 1
  if exist "%LAPLACE_ROOT%\build-win" (
    echo removing build-win ...
    rmdir /s /q "%LAPLACE_ROOT%\build-win"
  )
  if exist "%LAPLACE_ROOT%\build-win-ext" (
    echo removing build-win-ext ...
    rmdir /s /q "%LAPLACE_ROOT%\build-win-ext"
  )
) else (
  echo.
  echo ===== PHASE 1 — CLEAN [skipped: --skip-clean] =====
)

echo.
echo ===== PHASE 2 — CODEGEN =====
powershell -NoProfile -ExecutionPolicy Bypass -File "%LAPLACE_ROOT%\scripts\codegen-attestation-law.ps1" || exit /b 1

echo.
echo ===== PHASE 3 — BUILD ENGINE =====
call "%~dp0build-engine.cmd" || exit /b 1

echo.
echo ===== PHASE 4 — BUILD EXTENSIONS =====
call "%~dp0build-extensions.cmd" || exit /b 1

echo.
echo ===== PHASE 5 — DEPLOY / INSTALL =====
call "%~dp0install-extensions.cmd" || exit /b 1

if "%SKIP_APP%"=="0" (
  echo.
  echo ===== PHASE 6 — BUILD APP =====
  cd "%LAPLACE_ROOT%\app"
  dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1
  cd /d "%LAPLACE_ROOT%"
)

echo.
echo ===== PHASE 7 — PERF CACHE =====
cmake --build build-win --target laplace_t0_perfcache || exit /b 1
if not exist "%LAPLACE_PERFCACHE_BIN%" (
  echo ERROR: perf-cache blob missing at %LAPLACE_PERFCACHE_BIN%
  exit /b 1
)
for %%F in ("%LAPLACE_PERFCACHE_BIN%") do echo perfcache ready: %%~zF bytes — %%F

echo.
echo ===== REBUILD-ALL COMPLETE =====
exit /b 0
