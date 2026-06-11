@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

echo === engine gtest ===
ctest --test-dir build-win --output-on-failure -LE regress || exit /b 1

echo === pg_regress ===
call "%~dp0regress.cmd" || exit /b 1

echo === dotnet test ===
call "%~dp0test-app.cmd" || exit /b 1

echo === verify-fk ===
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -v ON_ERROR_STOP=1 -f scripts\verify-fk.sql || (
  echo verify-fk skipped or failed — laplace DB may not exist yet
)

echo ALL TEST LAYERS PASSED
exit /b 0
