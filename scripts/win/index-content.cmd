@echo off
setlocal
rem ==== Build the generation content index after seeding ========================
rem   scripts\win\index-content.cmd <database> [deep|text]      (default: deep)
rem deep = full constituency flattened to the token floor (code + prose + any
rem        grammar modality — the index the chat/completions endpoint serves).
rem text = tier-3 sentence trajectories only (legacy text law).
rem Runs ANALYZE first (fresh bulk loads have no planner stats), then the
rem native rebuild procedure, then reports index size. Idempotent.
rem ==============================================================================
if "%~1"=="" (
    echo usage: index-content.cmd ^<database^> [deep^|text]
    exit /b 2
)
call "%~dp0env.cmd"
set "MODE=%~2"
if "%MODE%"=="" set "MODE=deep"
if /i "%MODE%"=="deep" (
    set "CALLSQL=CALL laplace.rebuild_content_index_deep();"
) else (
    set "CALLSQL=CALL laplace.rebuild_content_index();"
)

echo ==== analyze %~1 ====
"%PGBIN%\vacuumdb.exe" -h localhost -U postgres -d %~1 --analyze-only || exit /b 1

echo ==== %MODE% content index on %~1 ====
rem CALL must run alone with connection-level search_path: a procedure invoked in
rem a multi-statement -c runs atomic and rejects its internal COMMITs.
set "PGOPTIONS=-c search_path=laplace,public"
"%PGBIN%\psql.exe" -h localhost -U postgres -d %~1 -v ON_ERROR_STOP=1 -c "%CALLSQL%" || exit /b 1
"%PGBIN%\psql.exe" -h localhost -U postgres -d %~1 -tAc "SELECT count(*) || ' positions, ' || count(DISTINCT seq_id) || ' sequences' FROM laplace.content_index;"
