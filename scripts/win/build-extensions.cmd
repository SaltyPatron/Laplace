@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd" || exit /b 1
rem The extension imports these archives from the engine build. Refresh them
rem before linking; an existing archive can belong to an older source revision.
call "%~dp0build-engine.cmd" laplace_core_static laplace_dynamics_pg_static laplace_query_program || exit /b 1
pwsh -NoProfile -File "%~dp0assert-build-source.ps1" -SourceDirectory "%LAPLACE_ROOT%\extension" -BuildDirectory "%LAPLACE_EXT_BUILD%" || exit /b 1
set "LAPLACE_RESOURCE_FLAGS="
if defined LAPLACE_RC set LAPLACE_RESOURCE_FLAGS="-DCMAKE_RC_COMPILER=%LAPLACE_RC%"
if defined LAPLACE_MT set LAPLACE_RESOURCE_FLAGS=%LAPLACE_RESOURCE_FLAGS% "-DCMAKE_MT=%LAPLACE_MT%"
cd /d "%LAPLACE_ROOT%"

set "RECONF=0"
set "CLEAN_FIRST=0"
set "SKIP_CODEGEN=0"
set "TARGETS="
:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--reconfigure" ( set "RECONF=1" & shift /1 & goto parse )
if /i "%~1"=="--clean-first" ( set "CLEAN_FIRST=1" & shift /1 & goto parse )
if /i "%~1"=="--skip-codegen" ( set "SKIP_CODEGEN=1" & shift /1 & goto parse )
set "TARGETS=!TARGETS! %~1"
shift /1
goto parse
:parsed

rem The canonical migration also upgrades the shared laplace extension. Build
rem its current module, not only laplace_geom and laplace_substrate.
set "PG_SOURCE=%LAPLACE_SRC%\Laplace-postgres"
pwsh -NoProfile -File "%~dp0assert-build-source.ps1" -SourceDirectory "%PG_SOURCE%" -BuildDirectory "%LAPLACE_PG_EXTENSION_BUILD%" || exit /b 1
pwsh -NoProfile -File "%~dp0tree-lock.ps1" acquire build-pg-extension || exit /b 1
if "%RECONF%"=="1" goto configure_pg
if not exist "%LAPLACE_PG_EXTENSION_BUILD%\CMakeCache.txt" goto configure_pg
if not exist "%LAPLACE_PG_EXTENSION_BUILD%\CMakeFiles\rules.ninja" goto configure_pg
if exist "%LAPLACE_PG_EXTENSION_BUILD%\build.ninja" goto build_pg
:configure_pg
cmake -S "%PG_SOURCE%" -B "%LAPLACE_PG_EXTENSION_BUILD%" -G Ninja ^
  -DCMAKE_BUILD_TYPE=RelWithDebInfo -DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icx ^
  %LAPLACE_RESOURCE_FLAGS% ^
  "-DPG_CONFIG=%PGBIN:\=/%/pg_config.exe" ^
  "-DLAPLACE_NATIVE=%LAPLACE_NATIVE:\=/%" ^
  "-DLAPLACE_BLAKE3_DIR=%LAPLACE_BLAKE3_DIR:\=/%" ^
  "-DLAPLACE_COREMATH=%LAPLACE_COREMATH:\=/%" ^
  "-DLAPLACE_OPERATIONS=%LAPLACE_OPERATIONS:\=/%" ^
  "-DLAPLACE_ICU_DIR=%LAPLACE_ICU_DIR:\=/%" ^
  "-DLAPLACE_TIER0=%LAPLACE_TIER0:\=/%"
if errorlevel 1 goto pg_fail
:build_pg
cmake --build "%LAPLACE_PG_EXTENSION_BUILD%" --target laplace_pg
if errorlevel 1 goto pg_fail
pwsh -NoProfile -File "%~dp0tree-lock.ps1" release build-pg-extension || exit /b 1

if "%SKIP_CODEGEN%"=="0" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%LAPLACE_ROOT%\scripts\codegen-attestation-law.ps1" || exit /b 1
) else (
  echo build-extensions: codegen skipped (--skip-codegen^)
)

pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-win-ext || exit /b 1

set "GEOS_PREFIX=%LAPLACE_DEPENDENCY_GEOS_PREFIX%"
if not defined GEOS_PREFIX set "GEOS_PREFIX=%LAPLACE_DEPS_PREFIX%\geos"
set "PROJ_PREFIX=%LAPLACE_DEPENDENCY_PROJ_PREFIX%"
if not defined PROJ_PREFIX set "PROJ_PREFIX=%LAPLACE_DEPS_PREFIX%\proj"
if not exist "%GEOS_PREFIX%\include\geos_c.h" (
  echo ERROR: geos missing under %GEOS_PREFIX%
  goto fail
)
if not exist "%PROJ_PREFIX%\include\proj.h" (
  echo ERROR: proj missing under %PROJ_PREFIX%
  goto fail
)

if "%RECONF%"=="1" goto configure
if not exist "%LAPLACE_EXT_BUILD%\build.ninja" goto configure
goto build

:configure
if exist "%LAPLACE_EXT_BUILD%\CMakeCache.txt" if not exist "%LAPLACE_EXT_BUILD%\build.ninja" (
  echo clearing dead-configure debris from %LAPLACE_EXT_BUILD%...
  del /q "%LAPLACE_EXT_BUILD%\CMakeCache.txt"
  rmdir /s /q "%LAPLACE_EXT_BUILD%\CMakeFiles" 2>nul
)
cmake -B "%LAPLACE_EXT_BUILD%" -S extension -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icx ^
  %LAPLACE_RESOURCE_FLAGS% ^
  "-DLAPLACE_PG_CONFIG=%PGBIN:\=/%/pg_config.exe" ^
  "-DLAPLACE_ENGINE_BUILD=%LAPLACE_ENGINE_BUILD:\=/%" ^
  "-DLAPLACE_DEPS_PREFIX=%LAPLACE_DEPS_PREFIX:\=/%"
if errorlevel 1 goto fail

:build
set "BUILD_FLAGS="
if "%CLEAN_FIRST%"=="1" set "BUILD_FLAGS=--clean-first"
if defined TARGETS (
  cmake --build "%LAPLACE_EXT_BUILD%" %BUILD_FLAGS% --target%TARGETS%
) else (
  cmake --build "%LAPLACE_EXT_BUILD%" %BUILD_FLAGS%
)
if errorlevel 1 goto fail

pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win-ext
exit /b 0

:fail
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win-ext
exit /b 1

:pg_fail
pwsh -NoProfile -File "%~dp0tree-lock.ps1" release build-pg-extension
exit /b 1
