@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

rem Build geos + sqlite + proj + gdal from external/ into LAPLACE_DEPS_PREFIX.
rem Same sources CI builds on Linux; Windows install prefix is under D:\Data\Laplace\deps.
rem
rem Compiler: MSVC cl (not icx). PROJ's IntelLLVM CMake path appends -fno-fast-math
rem into CMAKE_CXX_FLAGS and corrupts /EHsc under icx MSVC-mode. Deps are plain
rem C/C++ DLLs — MSVC ABI matches what icx-built laplace_geom imports.

if not defined LAPLACE_DEPS_PREFIX (
  echo ERROR: LAPLACE_DEPS_PREFIX unset — env.cmd must define it
  exit /b 1
)
if not defined LAPLACE_DEPS_BUILD set "LAPLACE_DEPS_BUILD=%LAPLACE_BUILD_ROOT%\build-deps"

set "VCVARS=D:\Microsoft Visual Studio\2026\VC\Auxiliary\Build\vcvarsall.bat"
if not exist "%VCVARS%" (
  echo ERROR: missing %VCVARS%
  exit /b 1
)
call "%VCVARS%" x64 >nul
if errorlevel 1 (
  echo ERROR: vcvarsall x64 failed
  exit /b 1
)

if not exist "%LAPLACE_DEPS_PREFIX%" mkdir "%LAPLACE_DEPS_PREFIX%"
if not exist "%LAPLACE_DEPS_BUILD%" mkdir "%LAPLACE_DEPS_BUILD%"

set "NINJA=D:/Microsoft Visual Studio/2026/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe"
set "DEPS_SRC=%LAPLACE_ROOT%\cmake\windows-deps"

echo ==== build-deps: configure %LAPLACE_DEPS_BUILD% ====
echo   prefix: %LAPLACE_DEPS_PREFIX%
echo   compiler: MSVC cl (deps only; engine/extensions stay on icx)
echo   sources: %LAPLACE_ROOT%\external\{geos,proj,gdal} + sqlite amalgamation

cmake -B "%LAPLACE_DEPS_BUILD%" -S "%DEPS_SRC%" -G Ninja ^
  "-DCMAKE_MAKE_PROGRAM=%NINJA%" ^
  -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl ^
  "-DCMAKE_RC_COMPILER=%LAPLACE_RC%" "-DCMAKE_MT=%LAPLACE_MT%" ^
  "-DLAPLACE_DEPS_PREFIX=%LAPLACE_DEPS_PREFIX:\=/%" ^
  "-DLAPLACE_EXTERNAL=%LAPLACE_ROOT:\=/%/external" ^
  "-DLAPLACE_DEPS_BUILD=%LAPLACE_DEPS_BUILD:\=/%"
if errorlevel 1 (
  echo [build-deps] configure FAILED
  exit /b 1
)

echo ==== build-deps: build + install geos/sqlite/proj/gdal ====
cmake --build "%LAPLACE_DEPS_BUILD%"
if errorlevel 1 (
  echo [build-deps] build FAILED
  exit /b 1
)

call :require_file "%LAPLACE_DEPS_PREFIX%\geos\include\geos_c.h" || exit /b 1
call :require_file "%LAPLACE_DEPS_PREFIX%\geos\lib\geos_c.lib" || exit /b 1
call :require_file "%LAPLACE_DEPS_PREFIX%\geos\lib\geos.lib" || exit /b 1
call :require_file "%LAPLACE_DEPS_PREFIX%\proj\include\proj.h" || exit /b 1
call :require_file "%LAPLACE_DEPS_PREFIX%\proj\lib\proj.lib" || exit /b 1
call :require_file "%LAPLACE_DEPS_PREFIX%\sqlite\include\sqlite3.h" || exit /b 1
call :require_file "%LAPLACE_DEPS_PREFIX%\sqlite\lib\sqlite3.lib" || exit /b 1
call :require_file "%LAPLACE_DEPS_PREFIX%\gdal\include\gdal.h" || exit /b 1

echo.
echo [build-deps] OK — %LAPLACE_DEPS_PREFIX%
echo   geos/proj/sqlite are STATIC libs linked into laplace_geom ^(no DLL shadowing by PG bin^)
echo   gdal is shared under %LAPLACE_DEPS_PREFIX%\gdal
echo   next: scripts\win\build-extensions.cmd
exit /b 0

:require_file
if not exist "%~1" (
  echo ERROR: missing %~1
  exit /b 1
)
echo   ok %~1
exit /b 0
