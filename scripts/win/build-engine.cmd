@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

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

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-win || exit /b 1

if "%RECONF%"=="1" goto configure
if not exist build-win\build.ninja goto configure
goto build

:configure
if exist build-win\CMakeCache.txt if not exist build-win\build.ninja (
  echo clearing dead-configure debris from build-win...
  del /q build-win\CMakeCache.txt
  rmdir /s /q build-win\CMakeFiles 2>nul
)
set "LAPLACE_UCD=%LAPLACE_DATA_ROOT%\UCD\Public\UCD\latest"
cmake -B build-win -S engine -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release ^
  "-DCMAKE_MAKE_PROGRAM=D:/Microsoft Visual Studio/2026/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe" ^
  -DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icx ^
  "-DCMAKE_RC_COMPILER=%LAPLACE_RC%" "-DCMAKE_MT=%LAPLACE_MT%" ^
  -DCMAKE_WINDOWS_EXPORT_ALL_SYMBOLS=ON ^
  -DBLAKE3_SIMD_TYPE=none ^
  -DBUILD_TESTING=ON ^
  -DLAPLACE_REQUIRE_MKL=ON ^
  -DLAPLACE_SYNTHESIS_REQUIRE_MKL=ON ^
  "-DLAPLACE_UCD_PATH=%LAPLACE_UCD%" ^
  "-DLAPLACE_UCDXML_ZIP=%LAPLACE_UCD%\ucdxml\ucd.nounihan.flat.zip" ^
  "-DLAPLACE_DUCET_FILE=%LAPLACE_UCD%\uca\allkeys.txt" ^
  "-DLAPLACE_UCD_CONFORMANCE_DIR=%LAPLACE_UCD%\ucd" ^
  "-DLIBXML2_INCLUDE_DIR=C:/Program Files/PostgreSQL/18/include" ^
  "-DLIBXML2_LIBRARY=C:/Program Files/PostgreSQL/18/lib/libxml2.lib"
if errorlevel 1 goto fail

:build
set "BUILD_FLAGS="
if "%CLEAN_FIRST%"=="1" set "BUILD_FLAGS=--clean-first"
if defined TARGETS (
  cmake --build build-win %BUILD_FLAGS% --target%TARGETS%
) else (
  cmake --build build-win %BUILD_FLAGS%
)
if errorlevel 1 goto fail

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win
exit /b 0

:fail
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win
exit /b 1
