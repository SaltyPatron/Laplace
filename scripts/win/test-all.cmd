@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
set "FAIL=0"
set "LOG=%LAPLACE_EXT_BUILD%\test-all.log"
if not exist "%LAPLACE_EXT_BUILD%" mkdir "%LAPLACE_EXT_BUILD%"
> "%LOG%" echo test-all started %DATE% %TIME%

echo === verify-toolchain ===
>> "%LOG%" echo === verify-toolchain ===
rem Full ctest runs below — skip the toolchain subset to avoid double work.
call "%~dp0verify-toolchain.cmd" --skip-ctest >> "%LOG%" 2>&1
if errorlevel 1 set "FAIL=1"

set "_ctest_j="
if defined LAPLACE_TEST_SERIAL (
  set "_ctest_j=-j1"
) else if defined CTEST_PARALLEL_LEVEL (
  set "_ctest_j=-j %CTEST_PARALLEL_LEVEL%"
)

if defined LAPLACE_TEST_SERIAL goto serial_layers

echo === engine gtest || pg_regress (parallel) ===
>> "%LOG%" echo === engine gtest || pg_regress (parallel) ===
set "CTEST_LOG=%LAPLACE_EXT_BUILD%\test-all-ctest.log"
set "REGRESS_LOG=%LAPLACE_EXT_BUILD%\test-all-regress.log"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$root='%LAPLACE_ROOT%'; $eng='%LAPLACE_ENGINE_BUILD%'; $j='%_ctest_j%'.Trim();" ^
  "$ctestLog='%CTEST_LOG%'; $regressLog='%REGRESS_LOG%'; $scripts=Join-Path $root 'scripts\win';" ^
  "$ctestArgs=@('--test-dir',$eng,'--output-on-failure','-LE','regress');" ^
  "if ($j) { $ctestArgs += $j.Split(' ',[System.StringSplitOptions]::RemoveEmptyEntries) };" ^
  "$cOut=$ctestLog; $cErr=$ctestLog+'.err'; $rOut=$regressLog; $rErr=$regressLog+'.err';" ^
  "$c=Start-Process -FilePath 'ctest' -ArgumentList $ctestArgs -WorkingDirectory $root -PassThru -NoNewWindow -RedirectStandardOutput $cOut -RedirectStandardError $cErr;" ^
  "$r=Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', (Join-Path $scripts 'regress.cmd')) -WorkingDirectory $root -PassThru -NoNewWindow -RedirectStandardOutput $rOut -RedirectStandardError $rErr;" ^
  "Wait-Process -Id $c.Id,$r.Id;" ^
  "Write-Host '---- ctest ----'; Get-Content $cOut,$cErr -ErrorAction SilentlyContinue;" ^
  "Write-Host '---- regress ----'; Get-Content $rOut,$rErr -ErrorAction SilentlyContinue;" ^
  "if ($c.ExitCode -ne 0 -or $r.ExitCode -ne 0) { exit 1 }; exit 0" >> "%LOG%" 2>&1
if errorlevel 1 set "FAIL=1"
goto after_layers

:serial_layers
echo === engine gtest ===
>> "%LOG%" echo === engine gtest ===
ctest --test-dir "%LAPLACE_ENGINE_BUILD%" --output-on-failure !_ctest_j! -LE regress >> "%LOG%" 2>&1
if errorlevel 1 set "FAIL=1"

echo === pg_regress ===
>> "%LOG%" echo === pg_regress ===
call "%~dp0regress.cmd" >> "%LOG%" 2>&1
if errorlevel 1 set "FAIL=1"

:after_layers
echo === dotnet test ===
>> "%LOG%" echo === dotnet test ===
set "CONTINUE_ON_FAIL=1"
call "%~dp0test-app.cmd" >> "%LOG%" 2>&1
if errorlevel 1 set "FAIL=1"
set "CONTINUE_ON_FAIL="

echo === verify-fk ===
>> "%LOG%" echo === verify-fk ===

if "%FAIL%"=="0" (
  "%PGBIN%\psql.exe" -h localhost -U postgres -d laplace_regress_substrate -v ON_ERROR_STOP=1 -f scripts\verify-fk.sql >> "%LOG%" 2>&1
  if errorlevel 1 set "FAIL=1"
) else (
  echo verify-fk skipped — prior test layer failed >> "%LOG%"
)

if "%FAIL%"=="0" (
  echo ALL TEST LAYERS PASSED
  >> "%LOG%" echo ALL TEST LAYERS PASSED
  exit /b 0
)
echo ONE OR MORE TEST LAYERS FAILED — see %LOG%
>> "%LOG%" echo ONE OR MORE TEST LAYERS FAILED
exit /b 1
