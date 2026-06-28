@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

rem The substrate SQL pulls in sql/generated/21_seed_*.sql.in (codegen output, not committed).
rem Regenerate it before configure so the file(GLOB sql/generated/*.sql.in) sees it. Mirrors
rem rebuild-all phase 2; harmless/idempotent when run via rebuild-all (which already codegens).
powershell -NoProfile -ExecutionPolicy Bypass -File "%LAPLACE_ROOT%\scripts\codegen-attestation-law.ps1" || exit /b 1

set "RECONF=0"
set "CLEAN_FIRST=0"
set "TARGETS="
:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--reconfigure" ( set "RECONF=1" & shift /1 & goto parse )
if /i "%~1"=="--clean-first" ( set "CLEAN_FIRST=1" & shift /1 & goto parse )
set "TARGETS=!TARGETS! %~1"
shift /1
goto parse
:parsed

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-win-ext || exit /b 1

if "%RECONF%"=="1" goto configure
if not exist build-win-ext\build.ninja goto configure
goto build

:configure
if exist build-win-ext\CMakeCache.txt if not exist build-win-ext\build.ninja (
  echo clearing dead-configure debris from build-win-ext...
  del /q build-win-ext\CMakeCache.txt
  rmdir /s /q build-win-ext\CMakeFiles 2>nul
)
cmake -B build-win-ext -S extension -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release ^
  "-DCMAKE_MAKE_PROGRAM=D:/Microsoft Visual Studio/2026/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe" ^
  -DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icx ^
  "-DCMAKE_RC_COMPILER=%LAPLACE_RC%" "-DCMAKE_MT=%LAPLACE_MT%"
if errorlevel 1 goto fail

:build
set "BUILD_FLAGS="
if "%CLEAN_FIRST%"=="1" set "BUILD_FLAGS=--clean-first"
if defined TARGETS (
  cmake --build build-win-ext %BUILD_FLAGS% --target%TARGETS%
) else (
  cmake --build build-win-ext %BUILD_FLAGS%
)
if errorlevel 1 goto fail

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win-ext
exit /b 0

:fail
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win-ext
exit /b 1
