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


set "LAPLACE_INGEST_LANGS=en"
if not defined LAPLACE_EMIT_CROSS_LANG set "LAPLACE_EMIT_CROSS_LANG=0"
if not defined LAPLACE_INGEST_WORKERS set "LAPLACE_INGEST_WORKERS=4"
set "LAPLACE_DECOMPOSE_WORKERS=1"
set "LAPLACE_COPY_VALIDATE=1"
if not defined LAPLACE_FOLD_WORKERS set "LAPLACE_FOLD_WORKERS=8"
if not defined LAPLACE_SKIP_USAGE set "LAPLACE_SKIP_USAGE=1"
if not defined LAPLACE_SKIP_LEXICAL_BULK set "LAPLACE_SKIP_LEXICAL_BULK=0"

if "%SKIP_CLEAN%"=="0" (
  echo.
  echo ===== PHASE 1 — CLEAN =====
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-win || exit /b 1
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-win-ext || exit /b 1
  if exist "%LAPLACE_ROOT%\build-win" (
    echo removing build-win ...
    rmdir /s /q "%LAPLACE_ROOT%\build-win"
  )
  if exist "%LAPLACE_ROOT%\build-win-ext" (
    echo removing build-win-ext ...
    rmdir /s /q "%LAPLACE_ROOT%\build-win-ext"
  )
  echo terminating laplace backends + drop database ...
  "%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='laplace' AND pid<>pg_backend_pid();" -c "DROP DATABASE IF EXISTS laplace;" || exit /b 1
) else (
  echo.
  echo ===== PHASE 1 — CLEAN [skipped: --skip-clean] =====
)

if "%DB_ONLY%"=="1" goto phase_db_bootstrap

echo.
echo ===== PHASE 2 — CODEGEN =====
powershell -NoProfile -ExecutionPolicy Bypass -File "%LAPLACE_ROOT%\scripts\codegen-attestation-law.ps1" || exit /b 1

echo.
echo ===== PHASE 3 — BUILD ENGINE =====
call "%~dp0build-engine.cmd" || exit /b 1

echo.
echo ===== PHASE 4 — BUILD EXTENSIONS =====
call "%~dp0build-extensions.cmd" || exit /b 1
call "%~dp0install-extensions.cmd" || exit /b 1

echo.
echo ===== PHASE 5 — BUILD APP =====
cd "%LAPLACE_ROOT%\app"
dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1
for %%P in (
  Laplace.Engine.Core
  Laplace.Engine.Dynamics
  Laplace.Engine.Synthesis
  Laplace.SubstrateCRUD
  Laplace.Ingestion
  Laplace.Decomposers.Abstractions
  Laplace.Decomposers.Model
  Laplace.Decomposers.Unicode
  Laplace.Decomposers.WordNet
) do (
  if exist "%%P\%%P.csproj" dotnet build "%%P\%%P.csproj" -c Release -v q --nologo || exit /b 1
)
cd /d "%LAPLACE_ROOT%"

echo.
echo ===== PHASE 6 — PERF CACHE =====
cmake --build build-win --target laplace_t0_perfcache || exit /b 1
if not exist "%LAPLACE_PERFCACHE_BIN%" (
  echo ERROR: perf-cache blob missing at %LAPLACE_PERFCACHE_BIN%
  echo   build-engine should emit laplace_t0_perfcache.bin — check UCD inputs in build-engine.cmd
  exit /b 1
)
for %%F in ("%LAPLACE_PERFCACHE_BIN%") do echo perfcache ready: %%~zF bytes — %%F

:phase_db_bootstrap
echo.
echo ===== PHASE 7 — DB BOOTSTRAP =====
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='laplace'" | findstr 1 >nul
if errorlevel 1 (
  echo creating database laplace ...
  "%PGBIN%\createdb.exe" -h localhost -U postgres laplace || exit /b 1
)
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS postgis;" -c "CREATE EXTENSION IF NOT EXISTS laplace_geom;" -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate;" || exit /b 1
echo post-create identity health:
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -v ON_ERROR_STOP=1 -c "SET search_path = laplace, public; SELECT * FROM substrate_health();" || exit /b 1

if "%DB_ONLY%"=="1" (
  echo.
  echo ===== E2E-MASTER DB-ONLY COMPLETE =====
  exit /b 0
)

cd "%LAPLACE_ROOT%\app"
dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1

echo.
echo ===== PHASE 8 — WITNESS LADDER =====
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
echo ===== PHASE 9 — VERIFY =====
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
