@echo off
setlocal
call "%~dp0env.cmd"
set "PSQL="%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1"

rem On hybrid Intel CPUs, consider capping max_parallel_workers near P-core count (laplace cpu-topology --p-cores).
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
 -c "ALTER SYSTEM SET max_parallel_workers = 24;" ^
 -c "ALTER SYSTEM SET max_parallel_workers_per_gather = 8;" ^
 -c "ALTER SYSTEM SET max_parallel_maintenance_workers = 8;" ^
 -c "ALTER SYSTEM SET wal_buffers = '128MB';" ^
 -c "ALTER SYSTEM SET effective_io_concurrency = 256;" ^
 -c "ALTER SYSTEM SET random_page_cost = 1.1;" ^
 -c "ALTER SYSTEM SET autovacuum_vacuum_cost_delay = 0;" ^
 || exit /b 1

%PSQL% -c "SELECT pg_reload_conf();" || exit /b 1

powershell -NoProfile -Command "Restart-Service postgresql-x64-18 -ErrorAction Stop" && goto verify
echo restart denied: run elevated:  net stop postgresql-x64-18 ^&^& net start postgresql-x64-18
exit /b 3

:verify
%PSQL% -P pager=off -c "SELECT name, setting, unit, pending_restart FROM pg_settings WHERE name IN ('shared_buffers','effective_cache_size','synchronous_commit','max_wal_size','work_mem','max_connections','max_parallel_workers','wal_buffers','random_page_cost','effective_io_concurrency') ORDER BY name;"
