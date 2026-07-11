@echo off
setlocal EnableDelayedExpansion
rem ============================================================================
rem  qa-isolated.cmd — whole-stack proof on a THROWAWAY live database.
rem
rem  fresh laplace_qa_* DB -> extensions -> seed unicode/iso639/cili/wordnet
rem  (witness-manifest cadence; a few minutes) -> invariant probes on known
rem  content -> DROP on success, kept + named on failure for inspection.
rem  Never touches laplace, laplace_regress_*, or any long-lived database.
rem  Requires: local PG up, extensions installed (install-extensions.cmd),
rem  no other Laplace.Cli ingest running (seed-step's mutex enforces this).
rem ============================================================================
set "LAPLACE_PGHOST=localhost"
set "LAPLACE_DBNAME=laplace_qa_%RANDOM%%RANDOM%"
set "LAPLACE_DB="
set "LAPLACE_ENV_LOADED="
call "%~dp0env.cmd"

echo ==== qa-isolated: %LAPLACE_DBNAME% @ localhost ====

"%PGBIN%\pg_isready.exe" -h localhost -q || (
  echo qa-isolated: local Postgres is down — nothing to prove against. & exit /b 3
)

echo ==== create + extend ====
"%PGBIN%\createdb.exe" -h localhost -U postgres %LAPLACE_DBNAME% || exit /b 1
"%PGBIN%\psql.exe" -h localhost -U postgres -d %LAPLACE_DBNAME% -v ON_ERROR_STOP=1 ^
  -c "CREATE EXTENSION IF NOT EXISTS postgis;" ^
  -c "CREATE EXTENSION IF NOT EXISTS laplace_geom;" ^
  -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate;" || goto :fail
"%PGBIN%\psql.exe" -h localhost -U postgres -d %LAPLACE_DBNAME% -P pager=off -v ON_ERROR_STOP=1 ^
  -c "SET search_path = laplace, public; SELECT * FROM substrate_health();" || goto :fail

echo ==== seed foundation slice (unicode -^> iso639 -^> cili -^> wordnet) ====
for %%S in (unicode iso639 cili wordnet) do (
  call "%~dp0seed-step.cmd" %%S || goto :fail
)

echo ==== invariant probes (known corpus) ====
set "SENSES="
rem bare psql from PATH — quoted-path executables inside for /f backticks lose
rem their leading quote (the 'C:\Program' is not recognized trap; lesson L8 class)
for /f "usebackq delims=" %%v in (`psql -h localhost -U postgres -d %LAPLACE_DBNAME% -tAc "SET search_path=laplace,public; SELECT senses(word_id('dog'));"`) do set "SENSES=%%v"
echo   senses(word_id('dog')) = !SENSES!
if not defined SENSES goto :fail
if "!SENSES!"=="0" goto :fail

echo ==== qa-isolated: PASS — dropping %LAPLACE_DBNAME% ====
"%PGBIN%\dropdb.exe" -h localhost -U postgres --force %LAPLACE_DBNAME%
exit /b 0

:fail
echo ==== qa-isolated: FAIL — database %LAPLACE_DBNAME% KEPT for inspection ====
echo   inspect: psql -h localhost -U postgres -d %LAPLACE_DBNAME%
echo   discard: dropdb -h localhost -U postgres --force %LAPLACE_DBNAME%
exit /b 1
