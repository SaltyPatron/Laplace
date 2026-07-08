@echo off
setlocal EnableDelayedExpansion

set "SKIP_CLEAN=0"
set "SKIP_MODELS=0"
set "DB_ONLY=0"

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--skip-clean"  ( set "SKIP_CLEAN=1"  & shift /1 & goto parse_args )
if /i "%~1"=="--skip-models" ( set "SKIP_MODELS=1" & shift /1 & goto parse_args )
if /i "%~1"=="--db-only"    ( set "DB_ONLY=1"     & shift /1 & goto parse_args )
echo unknown flag: %~1
exit /b 2

:args_done
if "%SKIP_MODELS%"=="1" set "LAPLACE_SKIP_MODELS=1"

call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

if not defined LAPLACE_EMIT_CROSS_LANG set "LAPLACE_EMIT_CROSS_LANG=0"
set "LAPLACE_COPY_VALIDATE=1"
if not defined LAPLACE_SKIP_USAGE set "LAPLACE_SKIP_USAGE=0"
if not defined LAPLACE_SKIP_LEXICAL_BULK set "LAPLACE_SKIP_LEXICAL_BULK=0"

if "%SKIP_CLEAN%"=="0" (
  call "%~dp0rebuild-all.cmd" || exit /b 1
) else (
  echo.
  echo ===== BUILD [incremental: --skip-clean] =====
  call "%~dp0rebuild-all.cmd" --skip-clean || exit /b 1
)

if "%DB_ONLY%"=="1" goto phase_db_bootstrap

:phase_db_bootstrap
echo.
echo ===== DB BOOTSTRAP =====
call "%~dp0db-reset.cmd" || exit /b 1

if "%DB_ONLY%"=="1" (
  echo.
  echo ===== E2E-MASTER DB-ONLY COMPLETE =====
  exit /b 0
)

echo.
echo ===== WITNESS LADDER =====
call "%~dp0seed-ladder.cmd" || exit /b 1

cd "%LAPLACE_ROOT%\app"
if exist "!INGEST!\test-data\text" (
  for %%f in ("!INGEST!\test-data\text\*.txt") do (
    echo ==== db-roundtrip proof %%~nxf ====
    dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- db-roundtrip "%%~f" || exit /b 1
  )
)

:phase_verify
echo.
echo ===== VERIFY =====
cd /d "%LAPLACE_ROOT%"

echo ==== engine gtest (excl. regress label) ====
call "%~dp0test-engine.cmd" -LE regress -j1 || exit /b 1

echo ==== pg_regress ====
call "%~dp0regress.cmd" || exit /b 1

echo ==== substrate audit ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -f "%LAPLACE_ROOT%\scripts\sql\substrate-audit.sql" || exit /b 1

echo ==== generation content index ====
call "%~dp0index-content.cmd" laplace deep || exit /b 1

echo ==== smoke: substrate_counts + consensus_stats ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -c "SELECT * FROM laplace.substrate_counts();" -c "SELECT * FROM laplace.consensus_stats();" -c "SELECT pg_size_pretty(pg_database_size('laplace')) AS db_size;" || exit /b 1

echo.
echo ===== E2E-MASTER COMPLETE =====
exit /b 0
