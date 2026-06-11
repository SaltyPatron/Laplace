@echo off
setlocal EnableDelayedExpansion
rem Build PG extensions into build-win-ext\ incrementally (deploy via install-extensions.cmd, NOT Program Files).
rem Usage: build-extensions.cmd [--reconfigure] [targets...]
rem Mutex-guarded like build-engine.cmd.
rem Agent rules: .github\instructions\build-environment.instructions.md
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

set "RECONF=0"
set "TARGETS="
:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--reconfigure" ( set "RECONF=1" & shift /1 & goto parse )
set "TARGETS=!TARGETS! %~1"
shift /1
goto parse
:parsed

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-win-ext || exit /b 1

if "%RECONF%"=="1" goto configure
if not exist build-win-ext\build.ninja goto configure
goto build

:configure
rem A cache without build.ninja = a configure that died mid-flight; its persisted compiler-detection
rem files poison every later configure. Clear them first (.lap-lock is preserved).
if exist build-win-ext\CMakeCache.txt if not exist build-win-ext\build.ninja (
  echo clearing dead-configure debris from build-win-ext...
  del /q build-win-ext\CMakeCache.txt
  rmdir /s /q build-win-ext\CMakeFiles 2>nul
)
rem CMAKE_MAKE_PROGRAM pinned: a tree configured with VS2022's older ninja writes a
rem .ninja_log the VS2026 ninja (PATH) cannot read -- every status dry-run then reported
rem 81 phantom steps and recompacted the log away, forcing full rebuilds (2026-06-10).
cmake -B build-win-ext -S extension -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release ^
  "-DCMAKE_MAKE_PROGRAM=D:/Microsoft Visual Studio/2026/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe" ^
  -DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icx ^
  "-DCMAKE_RC_COMPILER=%LAPLACE_RC%" "-DCMAKE_MT=%LAPLACE_MT%"
if errorlevel 1 goto fail

:build
if defined TARGETS (
  cmake --build build-win-ext --target%TARGETS%
) else (
  cmake --build build-win-ext
)
if errorlevel 1 goto fail

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win-ext
exit /b 0

:fail
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win-ext
exit /b 1
