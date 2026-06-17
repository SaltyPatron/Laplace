@echo off
setlocal
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
