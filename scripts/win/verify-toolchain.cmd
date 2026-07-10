@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
set "FAIL=0"
set "SKIP_CTEST=0"

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--skip-ctest" ( set "SKIP_CTEST=1" & shift /1 & goto parse_args )
echo verify-toolchain: unknown flag %~1
exit /b 2
:args_done

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

if not exist "%LAPLACE_ENGINE_BUILD%\CMakeCache.txt" (
  echo verify-toolchain: ERROR engine build not configured — run build-engine.cmd --reconfigure
  exit /b 1
)
findstr /C:"LAPLACE_REQUIRE_MKL:BOOL=ON" "%LAPLACE_ENGINE_BUILD%\CMakeCache.txt" >nul || (
  echo verify-toolchain: ERROR LAPLACE_REQUIRE_MKL is not ON — run build-engine.cmd --reconfigure
  set "FAIL=1"
)
findstr /C:"LAPLACE_SYNTHESIS_REQUIRE_MKL:BOOL=ON" "%LAPLACE_ENGINE_BUILD%\CMakeCache.txt" >nul || (
  echo verify-toolchain: ERROR LAPLACE_SYNTHESIS_REQUIRE_MKL is not ON — run build-engine.cmd --reconfigure
  set "FAIL=1"
)

if not exist "%LAPLACE_ENGINE_BUILD%\dynamics\laplace_dynamics.dll" (
  echo verify-toolchain: ERROR missing %LAPLACE_ENGINE_BUILD%\dynamics\laplace_dynamics.dll
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
  "%DUMPBIN%" /DEPENDENTS "%LAPLACE_ENGINE_BUILD%\dynamics\laplace_dynamics.dll" | findstr /i /c:"mkl_tbb" /c:"tbb12.dll" >nul || (
    echo verify-toolchain: ERROR laplace_dynamics.dll missing MKL/TBB runtime deps
    set "FAIL=1"
  )
  if "%FAIL%"=="0" echo verify-toolchain: laplace_dynamics.dll links mkl_tbb + tbb12
) else (
  echo verify-toolchain: WARN dumpbin not found — skipping DLL dependency check
)

if "%SKIP_CTEST%"=="1" (
  echo verify-toolchain: ctest skipped (--skip-ctest)
) else (
  set "_ctest_j="
  if defined LAPLACE_TEST_SERIAL (
    set "_ctest_j=-j1"
  ) else if defined CTEST_PARALLEL_LEVEL (
    set "_ctest_j=-j %CTEST_PARALLEL_LEVEL%"
  )
  ctest --test-dir "%LAPLACE_ENGINE_BUILD%" --output-on-failure !_ctest_j! -R "LaplaceDynamicsToolchain|SynthesisToolchain"
  if errorlevel 1 set "FAIL=1"
)

if "%FAIL%"=="0" (
  echo verify-toolchain: OK
  exit /b 0
)
echo verify-toolchain: FAILED
exit /b 1
