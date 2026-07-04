@echo off
setlocal
call "%~dp0env.cmd"
set "PSQL="%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -v ON_ERROR_STOP=1"

REM tune-laplace: db/table-scoped tuning for the laplace substrate (distinct from tune-pg,
REM which sets cluster-wide ALTER SYSTEM GUCs). These are ALTER TABLE settings, so they need
REM the substrate tables to already exist -- run this AFTER install/db-reset, never on an
REM empty cluster. Guarded below so it skips (not errors) when the tables aren't present yet.
REM
REM What it does / why: stats silently go stale on a 100M+ row DB. SET STATISTICS 0 skips the
REM minutes-long PostGIS ND-stats on the geometry columns (read via GiST/KNN, not histograms)
REM -> ANALYZE/autoanalyze on physicalities ~160x cheaper (measured 137s -> 0.87s). The 2%/100k
REM autoanalyze thresholds fire at 100M-row scale instead of the 10% default that lags on bulk
REM ingest. Without this the planner mis-costs the descent/apply existence probes (reltuples off
REM by ~14x), dropping ~17k ids/s to ~2.3k. Idempotent ALTERs; no restart needed.

for /f "usebackq delims=" %%i in (`%PSQL% -tAc "SELECT to_regclass('laplace.physicalities') IS NOT NULL" 2^>nul`) do set "HAVE=%%i"
if /i not "%HAVE%"=="t" (
  echo tune-laplace: substrate tables not present in 'laplace' yet -- skipping ^(run after install/db-reset^).
  exit /b 0
)

%PSQL% ^
 -c "ALTER TABLE laplace.physicalities ALTER COLUMN coord SET STATISTICS 0;" ^
 -c "ALTER TABLE laplace.physicalities ALTER COLUMN trajectory SET STATISTICS 0;" ^
 -c "ALTER TABLE laplace.entities      SET (autovacuum_analyze_scale_factor = 0.02, autovacuum_analyze_threshold = 100000);" ^
 -c "ALTER TABLE laplace.physicalities SET (autovacuum_analyze_scale_factor = 0.02, autovacuum_analyze_threshold = 100000);" ^
 -c "ALTER TABLE laplace.attestations  SET (autovacuum_analyze_scale_factor = 0.02, autovacuum_analyze_threshold = 100000);" ^
 -c "ALTER TABLE laplace.consensus     SET (autovacuum_analyze_scale_factor = 0.02, autovacuum_analyze_threshold = 100000);" ^
 || exit /b 1
echo tune-laplace: applied per-table stat + autoanalyze tuning to laplace.
