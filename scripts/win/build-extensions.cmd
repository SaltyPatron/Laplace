@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"




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
  "-DCMAKE_MAKE_PROGRAM=D:/Microsoft Visual Studio/2026/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe" ^
  -DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icx ^
  "-DCMAKE_RC_COMPILER=%LAPLACE_RC%" "-DCMAKE_MT=%LAPLACE_MT%" ^
  "-DLAPLACE_ENGINE_BUILD=%LAPLACE_ENGINE_BUILD:\=/%"
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

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win-ext
exit /b 0

:fail
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win-ext
exit /b 1
