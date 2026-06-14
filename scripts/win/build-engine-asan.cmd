@echo off
setlocal EnableDelayedExpansion
rem ASan engine tree: build-win-asan\ (RelWithDebInfo + -fsanitize=address, icx).
rem icx hard-rejects ASan combined with the debug CRT (-MDd), and CMake's compiler ABI probe
rem runs in the Debug config by default -- so this script pins the dynamic release CRT
rem (CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL) and the try_compile configuration.
rem Do NOT configure this tree by hand; that is how the 2026-06-10 dead configure happened.
rem Run tests from this tree with oneAPI compiler bin on PATH (env.cmd does it; clang_rt.asan*.dll lives there):
rem   build-engine-asan.cmd laplace_core_tests && ctest --test-dir build-win-asan -R <area>
rem Usage: build-engine-asan.cmd [--reconfigure] [--configure-only] [targets...]
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
rem env.cmd prepends build-win\{core,dynamics,synthesis} to PATH; this tree's exes (perfcache emit,
rem gtest discovery, ctest) must resolve THEIR OWN DLLs, not the Release tree's -- shadow it.
set "PATH=%LAPLACE_ROOT%\build-win-asan\core;%LAPLACE_ROOT%\build-win-asan\dynamics;%LAPLACE_ROOT%\build-win-asan\synthesis;%PATH%"
rem icx's ASan runtime (clang_rt.asan_dynamic-x86_64.dll) lives under lib\clang\<ver>\lib\windows,
rem NOT in bin -- and VS's MSVC toolset on the machine PATH ships a DIFFERENT clang version's copy,
rem which loads but lacks entrypoints (0xC0000139). Pin Intel's, version-globbed.
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
rem A cache without build.ninja = a configure that died mid-flight. Its persisted compiler-detection
rem files (CMakeFiles\<ver>\*.cmake) poison every later configure (the literal-%%LAPLACE_RC%% incident),
rem so clear them before configuring. .lap-lock is preserved.
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
