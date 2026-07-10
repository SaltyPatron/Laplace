@echo off
setlocal EnableDelayedExpansion

set "SKIP_CLEAN=0"
set "SKIP_APP=0"
set "APP_TREE_WIPED=0"

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
    set "APP_TREE_WIPED=1"
  )
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win-ext
  powershell -NoProfile -ExecutionPolicy Bypass -Command ". '%~dp0laplace-paths.ps1'; Remove-StaleInRepoBuildArtifacts"
) else (
  echo.
  echo ===== PHASE 1 — CLEAN [skipped: --skip-clean; native uses --clean-first] =====
)

echo.
echo ===== PHASE 2 — CODEGEN =====
powershell -NoProfile -ExecutionPolicy Bypass -File "%LAPLACE_ROOT%\scripts\codegen-attestation-law.ps1" || exit /b 1

echo.
echo ===== PHASE 2b — EXTERNAL DEPS (geos/proj/gdal) =====
if exist "%LAPLACE_DEPS_PREFIX%\geos\include\geos_c.h" if exist "%LAPLACE_DEPS_PREFIX%\proj\include\proj.h" if exist "%LAPLACE_DEPS_PREFIX%\gdal\include\gdal.h" (
  echo deps already present under %LAPLACE_DEPS_PREFIX% — skipping build-deps
) else (
  call "%~dp0build-deps.cmd" || exit /b 1
)

echo.
echo ===== PHASE 3 — BUILD ENGINE =====
call "%~dp0build-engine.cmd" %NATIVE_FLAGS% || exit /b 1

echo.
echo ===== PHASE 4 — BUILD EXTENSIONS =====
rem Codegen just ran in Phase 2 — skip the duplicate PS1 invoke.
call "%~dp0build-extensions.cmd" --skip-codegen %NATIVE_FLAGS% || exit /b 1

echo.
echo ===== PHASE 5 — PERF CACHE (existence check; ALL targets already built them) =====
if not exist "%LAPLACE_PERFCACHE_BIN%" (
  echo ERROR: T0 perfcache blob missing at %LAPLACE_PERFCACHE_BIN%
  echo        engine ALL build should have emitted it — check laplace_t0_perfcache target
  exit /b 1
)
if not exist "%LAPLACE_HIGHWAY_PERFCACHE_BIN%" (
  echo ERROR: highway perfcache blob missing at %LAPLACE_HIGHWAY_PERFCACHE_BIN%
  echo        engine ALL build should have emitted it — check laplace_highway_perfcache target
  exit /b 1
)
for %%F in ("%LAPLACE_PERFCACHE_BIN%") do echo T0 perfcache ready: %%~zF bytes — %%F
for %%F in ("%LAPLACE_HIGHWAY_PERFCACHE_BIN%") do echo highway perfcache ready: %%~zF bytes — %%F

echo.
echo ===== PHASE 6 — DEPLOY / INSTALL =====
rem Extensions just built — skip redundant SQL target cmake hop.
call "%~dp0install-extensions.cmd" --skip-build || exit /b 1

if "%SKIP_APP%"=="0" (
  echo.
  echo ===== PHASE 7 — BUILD APP =====
  cd "%LAPLACE_ROOT%\app"
  if "%APP_TREE_WIPED%"=="0" (
    echo dotnet clean Release ...
    dotnet clean Laplace.slnx -c Release --nologo -v minimal || exit /b 1
  ) else (
    echo dotnet clean skipped — app tree wiped in Phase 1
  )
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
