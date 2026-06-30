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

set "NATIVE_FLAGS="
if "%SKIP_CLEAN%"=="1" set "NATIVE_FLAGS=--clean-first"

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
  if exist "%LAPLACE_ROOT%\app\Laplace.Cli\bin" (
    echo removing app Release bin ...
    rmdir /s /q "%LAPLACE_ROOT%\app\Laplace.Cli\bin"
  )
  if exist "%LAPLACE_ROOT%\app\Laplace.Cli\obj" (
    echo removing app Release obj ...
    rmdir /s /q "%LAPLACE_ROOT%\app\Laplace.Cli\obj"
  )
) else (
  echo.
  echo ===== PHASE 1 — CLEAN [skipped: --skip-clean; native uses --clean-first] =====
)

echo.
echo ===== PHASE 2 — CODEGEN =====
powershell -NoProfile -ExecutionPolicy Bypass -File "%LAPLACE_ROOT%\scripts\codegen-attestation-law.ps1" || exit /b 1

echo.
echo ===== PHASE 3 — BUILD ENGINE =====
call "%~dp0build-engine.cmd" %NATIVE_FLAGS% || exit /b 1

echo.
echo ===== PHASE 4 — BUILD EXTENSIONS =====
call "%~dp0build-extensions.cmd" %NATIVE_FLAGS% || exit /b 1

echo.
echo ===== PHASE 5 — DEPLOY / INSTALL =====
call "%~dp0install-extensions.cmd" || exit /b 1

if "%SKIP_APP%"=="0" (
  echo.
  echo ===== PHASE 6 — BUILD APP =====
  cd "%LAPLACE_ROOT%\app"
  echo dotnet clean Release ...
  dotnet clean Laplace.slnx -c Release --nologo -v minimal || exit /b 1
  echo dotnet build Release ...
  dotnet build Laplace.slnx -c Release -v minimal || exit /b 1
  cd /d "%LAPLACE_ROOT%"
)

echo.
echo ===== PHASE 7 — PERF CACHE =====
cmake --build build-win --target laplace_t0_perfcache laplace_highway_perfcache || exit /b 1
if not exist "%LAPLACE_PERFCACHE_BIN%" (
  echo ERROR: perf-cache blob missing at %LAPLACE_PERFCACHE_BIN%
  exit /b 1
)
if not exist "%LAPLACE_ROOT%\build-win\core\perfcache\laplace_highway_perfcache.bin" (
  echo ERROR: highway perfcache blob missing at build-win\core\perfcache\laplace_highway_perfcache.bin
  exit /b 1
)
for %%F in ("%LAPLACE_PERFCACHE_BIN%") do echo T0 perfcache ready: %%~zF bytes — %%F
for %%F in ("%LAPLACE_ROOT%\build-win\core\perfcache\laplace_highway_perfcache.bin") do echo highway perfcache ready: %%~zF bytes — %%F

echo.
echo ===== REBUILD-ALL COMPLETE =====
exit /b 0
