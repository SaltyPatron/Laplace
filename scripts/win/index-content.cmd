@echo off
setlocal
rem ==== Warm the generation corpus and report its stats =========================
rem   scripts\win\index-content.cmd <database>
rem The corpus is built per backend straight from physicalities.trajectory
rem (single source — no content_index/content_pairs tables exist). Building it
rem IS the warm-up; corpus_stats() is the receipt. Runs ANALYZE first so the
rem trajectory probe and edge scan plan well on fresh bulk loads. Idempotent.
rem ==============================================================================
if "%~1"=="" (
    echo usage: index-content.cmd ^<database^>
    exit /b 2
)
call "%~dp0env.cmd"

echo ==== analyze %~1 ====
"%PGBIN%\vacuumdb.exe" -h localhost -U postgres -d %~1 --analyze-only || exit /b 1

echo ==== corpus stats on %~1 (build = warm) ====
set "PGOPTIONS=-c search_path=laplace,public"
"%PGBIN%\psql.exe" -h localhost -U postgres -d %~1 -P pager=off -v ON_ERROR_STOP=1 -c "SELECT * FROM laplace.corpus_stats();" || exit /b 1
