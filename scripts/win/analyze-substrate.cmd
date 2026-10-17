@echo off
setlocal
call "%~dp0env.cmd" || exit /b 1
call "%~dp0require-dbname.cmd" || exit /b 2

rem analyze-substrate.cmd: refresh the planner statistics of %LAPLACE_DBNAME% after a seed.
rem The extension's tables belong to postgres (db-reset creates them as postgres), and the role a
rem seed runs as (LAPLACE_PGUSER, e.g. laplace_desktop_db) has no MAINTAIN on them. Its post-ingest
rem ANALYZE is then skipped table by table with only a WARNING, and the run still reports success
rem (Laplace#1879). Autovacuum never analyzes a partitioned parent either. Until ANALYZE runs as the
rem owner, every leaf keeps reltuples = -1 and laplace.attestations has no pg_stats, so
rem ops.substrate_counts() reads 0 and ops.source_counts_approx() returns no source: the site's
rem warehouse (/v1/explore/catalog) shows a seeded database as empty. This runs the installed
rem ops.analyze_substrate() as postgres, then prints the counts it was for.
echo ==== analyze-substrate: %LAPLACE_DBNAME% (as postgres, the tables' owner) ====
"%PGBIN%\psql.exe" -X -h localhost -U postgres -d "%LAPLACE_DBNAME%" -v ON_ERROR_STOP=1 -c "CALL ops.analyze_substrate();" || exit /b 1
"%PGBIN%\psql.exe" -X -h localhost -U postgres -d "%LAPLACE_DBNAME%" -P pager=off -v ON_ERROR_STOP=1 -c "SELECT metric, value FROM ops.substrate_counts();" || exit /b 1
exit /b 0
