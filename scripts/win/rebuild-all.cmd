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
  if exist "%LAPLACE_ENGINE_BUILD%" (
    echo removing %LAPLACE_ENGINE_BUILD% ...
    rmdir /s /q "%LAPLACE_ENGINE_BUILD%"
  )
  if exist "%LAPLACE_EXT_BUILD%" (
    echo removing %LAPLACE_EXT_BUILD% ...
    rmdir /s /q "%LAPLACE_EXT_BUILD%"
  )
  if exist "%LAPLACE_BUILD_ROOT%\app" (
    echo removing external app build tree ...
    rmdir /s /q "%LAPLACE_BUILD_ROOT%\app"
  )
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win-ext
  if exist "%LAPLACE_ROOT%\build-win" (
    echo removing stale in-repo build-win lock tree ...
    rmdir /s /q "%LAPLACE_ROOT%\build-win"
  )
  if exist "%LAPLACE_ROOT%\build-win-ext" (
    rmdir /s /q "%LAPLACE_ROOT%\build-win-ext"
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
echo ===== PHASE 5 — PERF CACHE (before deploy — install-extensions copies these) =====
cmake --build "%LAPLACE_ENGINE_BUILD%" --target laplace_t0_perfcache laplace_highway_perfcache || exit /b 1
if not exist "%LAPLACE_PERFCACHE_BIN%" (
  echo ERROR: T0 perfcache blob missing at %LAPLACE_PERFCACHE_BIN%
  exit /b 1
)
if not exist "%LAPLACE_HIGHWAY_PERFCACHE_BIN%" (
  echo ERROR: highway perfcache blob missing at %LAPLACE_HIGHWAY_PERFCACHE_BIN%
  exit /b 1
)
for %%F in ("%LAPLACE_PERFCACHE_BIN%") do echo T0 perfcache ready: %%~zF bytes — %%F
for %%F in ("%LAPLACE_HIGHWAY_PERFCACHE_BIN%") do echo highway perfcache ready: %%~zF bytes — %%F

echo.
echo ===== PHASE 6 — DEPLOY / INSTALL =====
call "%~dp0install-extensions.cmd" || exit /b 1

if "%SKIP_APP%"=="0" (
  echo.
  echo ===== PHASE 7 — BUILD APP =====
  cd "%LAPLACE_ROOT%\app"
  echo dotnet clean Release ...
  dotnet clean Laplace.slnx -c Release --nologo -v minimal || exit /b 1
  echo dotnet build Release ...
  dotnet build Laplace.slnx -c Release -v minimal || exit /b 1
  cd /d "%LAPLACE_ROOT%"
)

echo.
echo ===== PHASE 8 — PUBLISH + IIS DEPLOY =====
call "%~dp0publish-deploy.cmd" --skip-managed-build || exit /b 1

echo.
echo ===== REBUILD-ALL COMPLETE =====
exit /b 0
