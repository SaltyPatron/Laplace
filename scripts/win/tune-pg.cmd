@echo off
setlocal
call "%~dp0env.cmd"
set "PSQL="%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1"

rem Derive PG parallelism from the real P-core count. Hybrid CPU: keep parallel workers on the fast
rem P-cores (8 on a 14900KS), OFF the slow E-cores, and leave headroom for the client ingest pools
rem (compose/decompose/commit) and the interactive user. Falls back to 8 if the CLI isn't built yet.
set "PCORES=8"
pushd "%LAPLACE_ROOT%\app" >nul 2>&1
for /f "usebackq delims=" %%i in (`dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- cpu-topology --p-cores 2^>nul`) do set "PCORES=%%i"
popd >nul 2>&1
set /a "PCORES=PCORES" 2>nul || set "PCORES=8"
if %PCORES% lss 1 set "PCORES=8"
set /a "PGATHER=(PCORES+1)/2"
echo tune-pg: P-cores=%PCORES% -^> max_parallel_workers=%PCORES%, per_gather=%PGATHER%, maintenance_workers=%PGATHER%
%PSQL% ^
 -c "ALTER SYSTEM SET shared_buffers = '12GB';" ^
 -c "ALTER SYSTEM SET effective_cache_size = '32GB';" ^
 -c "ALTER SYSTEM SET maintenance_work_mem = '2GB';" ^
 -c "ALTER SYSTEM SET work_mem = '256MB';" ^
 -c "ALTER SYSTEM SET max_wal_size = '32GB';" ^
 -c "ALTER SYSTEM SET min_wal_size = '4GB';" ^
 -c "ALTER SYSTEM SET synchronous_commit = off;" ^
 -c "ALTER SYSTEM SET wal_compression = on;" ^
 -c "ALTER SYSTEM SET checkpoint_timeout = '30min';" ^
 -c "ALTER SYSTEM SET checkpoint_completion_target = 0.9;" ^
 -c "ALTER SYSTEM SET max_connections = 200;" ^
 -c "ALTER SYSTEM SET max_worker_processes = 32;" ^
 -c "ALTER SYSTEM SET max_parallel_workers = %PCORES%;" ^
 -c "ALTER SYSTEM SET max_parallel_workers_per_gather = %PGATHER%;" ^
 -c "ALTER SYSTEM SET max_parallel_maintenance_workers = %PGATHER%;" ^
 -c "ALTER SYSTEM SET wal_buffers = '128MB';" ^
 -c "ALTER SYSTEM SET effective_io_concurrency = 256;" ^
 -c "ALTER SYSTEM SET maintenance_io_concurrency = 256;" ^
 -c "ALTER SYSTEM SET random_page_cost = 1.1;" ^
 -c "ALTER SYSTEM SET autovacuum_vacuum_cost_delay = 0;" ^
 || exit /b 1

%PSQL% -c "SELECT pg_reload_conf();" || exit /b 1

powershell -NoProfile -Command "Restart-Service postgresql-x64-18 -ErrorAction Stop" && goto verify
echo restart denied: run elevated:  net stop postgresql-x64-18 ^&^& net start postgresql-x64-18
exit /b 3

:verify
%PSQL% -P pager=off -c "SELECT name, setting, unit, pending_restart FROM pg_settings WHERE name IN ('shared_buffers','effective_cache_size','synchronous_commit','max_wal_size','work_mem','max_connections','max_parallel_workers','wal_buffers','random_page_cost','effective_io_concurrency') ORDER BY name;"
