@echo off
setlocal EnableDelayedExpansion
rem Build engine into build-win\ incrementally (NOT cmake --install).
rem Usage: build-engine.cmd [--reconfigure] [targets...]
rem   no args        = configure only if needed, then full incremental build
rem   targets...     = build just those ninja targets (e.g. laplace_core laplace_core_tests)
rem   --reconfigure  = force a fresh CMake configure first
rem Mutex-guarded: a second build on build-win waits instead of corrupting the tree.
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

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-win || exit /b 1

if "%RECONF%"=="1" goto configure
if not exist build-win\build.ninja goto configure
goto build

:configure
rem A cache without build.ninja = a configure that died mid-flight; its persisted compiler-detection
rem files poison every later configure. Clear them first (.lap-lock is preserved).
if exist build-win\CMakeCache.txt if not exist build-win\build.ninja (
  echo clearing dead-configure debris from build-win...
  del /q build-win\CMakeCache.txt
  rmdir /s /q build-win\CMakeFiles 2>nul
)
cmake -B build-win -S engine -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release ^
  "-DCMAKE_MAKE_PROGRAM=D:/Microsoft Visual Studio/2026/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe" ^
  -DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icx ^
  "-DCMAKE_RC_COMPILER=%LAPLACE_RC%" "-DCMAKE_MT=%LAPLACE_MT%" ^
  -DCMAKE_WINDOWS_EXPORT_ALL_SYMBOLS=ON ^
  -DBLAKE3_SIMD_TYPE=none ^
  -DBUILD_TESTING=ON ^
  "-DLAPLACE_UCD_PATH=D:/Data/Ingest/UCD/Public/UCD/latest" ^
  "-DLAPLACE_UCDXML_ZIP=D:/Data/Ingest/UCD/Public/UCD/latest/ucdxml/ucd.nounihan.flat.zip" ^
  "-DLAPLACE_DUCET_FILE=D:/Data/Ingest/UCD/Public/UCD/latest/uca/allkeys.txt" ^
  "-DLAPLACE_UCD_CONFORMANCE_DIR=D:/Data/Ingest/UCD/Public/UCD/latest/ucd" ^
  "-DLIBXML2_INCLUDE_DIR=C:/Program Files/PostgreSQL/18/include" ^
  "-DLIBXML2_LIBRARY=C:/Program Files/PostgreSQL/18/lib/libxml2.lib"
if errorlevel 1 goto fail

:build
if defined TARGETS (
  cmake --build build-win --target%TARGETS%
) else (
  cmake --build build-win
)
if errorlevel 1 goto fail

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win
exit /b 0

:fail
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win
exit /b 1
