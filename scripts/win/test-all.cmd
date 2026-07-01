@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
set "FAIL=0"
set "LOG=%LAPLACE_ROOT%\build-win-ext\test-all.log"
if not exist "%LAPLACE_ROOT%\build-win-ext" mkdir "%LAPLACE_ROOT%\build-win-ext"
> "%LOG%" echo test-all started %DATE% %TIME%

echo === verify-toolchain ===
>> "%LOG%" echo === verify-toolchain ===
call "%~dp0verify-toolchain.cmd" >> "%LOG%" 2>&1
if errorlevel 1 set "FAIL=1"

echo === engine gtest ===
>> "%LOG%" echo === engine gtest ===
ctest --test-dir build-win --output-on-failure -LE regress >> "%LOG%" 2>&1
if errorlevel 1 set "FAIL=1"

echo === pg_regress ===
>> "%LOG%" echo === pg_regress ===
call "%~dp0regress.cmd" >> "%LOG%" 2>&1
if errorlevel 1 set "FAIL=1"

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
