@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
set "PATH=%LAPLACE_ROOT%\build-win-asan\core;%LAPLACE_ROOT%\build-win-asan\dynamics;%LAPLACE_ROOT%\build-win-asan\synthesis;%PATH%"
for /d %%v in ("C:\Program Files (x86)\Intel\oneAPI\compiler\latest\lib\clang\*") do set "LAPLACE_ASAN_RT=%%v\lib\windows"
set "PATH=%LAPLACE_ASAN_RT%;%PATH%"

set "RECONF=0"
set "CONFONLY=0"
set "TARGETS="
:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--reconfigure"    ( set "RECONF=1"   & shift /1 & goto parse )
if /i "%~1"=="--configure-only" ( set "CONFONLY=1" & shift /1 & goto parse )
set "TARGETS=!TARGETS! %~1"
shift /1
goto parse
:parsed

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-win-asan || exit /b 1

if "%RECONF%"=="1" goto configure
if not exist build-win-asan\build.ninja goto configure
goto build

:configure
if exist build-win-asan\CMakeCache.txt if not exist build-win-asan\build.ninja (
  echo clearing dead-configure debris from build-win-asan...
  del /q build-win-asan\CMakeCache.txt
  rmdir /s /q build-win-asan\CMakeFiles 2>nul
)
cmake -B build-win-asan -S engine -G Ninja ^
  -DCMAKE_BUILD_TYPE=RelWithDebInfo ^
  "-DCMAKE_MAKE_PROGRAM=D:/Microsoft Visual Studio/2026/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe" ^
  -DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icx ^
  "-DCMAKE_RC_COMPILER=%LAPLACE_RC%" "-DCMAKE_MT=%LAPLACE_MT%" ^
  "-DCMAKE_C_FLAGS=/DWIN32 /D_WINDOWS -fsanitize=address" ^
  "-DCMAKE_CXX_FLAGS=/DWIN32 /D_WINDOWS /EHsc -fsanitize=address" ^
  "-DCMAKE_EXE_LINKER_FLAGS=/Qoption,link,/machine:x64 -fsanitize=address /MD" ^
  "-DCMAKE_SHARED_LINKER_FLAGS=/Qoption,link,/machine:x64 -fsanitize=address /MD" ^
  "-DCMAKE_MODULE_LINKER_FLAGS=/Qoption,link,/machine:x64 -fsanitize=address /MD" ^
  -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL ^
  -DCMAKE_TRY_COMPILE_CONFIGURATION=RelWithDebInfo ^
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
if "%CONFONLY%"=="1" goto done
if defined TARGETS (
  cmake --build build-win-asan --target%TARGETS%
) else (
  cmake --build build-win-asan
)
if errorlevel 1 goto fail

:done
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win-asan
exit /b 0

:fail
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win-asan
exit /b 1
