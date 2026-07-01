@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
set "FAIL=0"

echo === verify-toolchain ===
for %%V in (MKLROOT TBBROOT CMPLR_ROOT) do (
  if not defined %%V (
    echo verify-toolchain: ERROR %%V unset — source env.cmd / setvars first
    set "FAIL=1"
  ) else (
    echo verify-toolchain: %%V=!%%V!
  )
)

echo verify-toolchain: MKL_NUM_THREADS=%MKL_NUM_THREADS% TBB_NUM_THREADS=%TBB_NUM_THREADS% MKL_DYNAMIC=%MKL_DYNAMIC%

if not exist build-win\CMakeCache.txt (
  echo verify-toolchain: ERROR build-win not configured — run build-engine.cmd --reconfigure
  exit /b 1
)
findstr /C:"LAPLACE_REQUIRE_MKL:BOOL=ON" build-win\CMakeCache.txt >nul || (
  echo verify-toolchain: ERROR LAPLACE_REQUIRE_MKL is not ON — run build-engine.cmd --reconfigure
  set "FAIL=1"
)
findstr /C:"LAPLACE_SYNTHESIS_REQUIRE_MKL:BOOL=ON" build-win\CMakeCache.txt >nul || (
  echo verify-toolchain: ERROR LAPLACE_SYNTHESIS_REQUIRE_MKL is not ON — run build-engine.cmd --reconfigure
  set "FAIL=1"
)

if not exist build-win\dynamics\laplace_dynamics.dll (
  echo verify-toolchain: ERROR missing build-win\dynamics\laplace_dynamics.dll
  set "FAIL=1"
)
if "%FAIL%"=="1" exit /b 1

set "DUMPBIN="
for /f "delims=" %%D in ('where dumpbin 2^>nul') do set "DUMPBIN=%%D"
if not defined DUMPBIN (
  for %%P in (
    "D:\Microsoft Visual Studio\2026\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64\dumpbin.exe"
    "D:\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64\dumpbin.exe"
  ) do if exist %%P set "DUMPBIN=%%~P"
)
if defined DUMPBIN (
  "%DUMPBIN%" /DEPENDENTS build-win\dynamics\laplace_dynamics.dll | findstr /i /c:"mkl_tbb" /c:"tbb12.dll" >nul || (
    echo verify-toolchain: ERROR laplace_dynamics.dll missing MKL/TBB runtime deps
    set "FAIL=1"
  )
  if "%FAIL%"=="0" echo verify-toolchain: laplace_dynamics.dll links mkl_tbb + tbb12
) else (
  echo verify-toolchain: WARN dumpbin not found — skipping DLL dependency check
)

ctest --test-dir build-win --output-on-failure -R "LaplaceDynamicsToolchain|SynthesisToolchain"
if errorlevel 1 set "FAIL=1"

if "%FAIL%"=="0" (
  echo verify-toolchain: OK
  exit /b 0
)
echo verify-toolchain: FAILED
exit /b 1
