@echo off
setlocal
set "PGPASSWORD=postgres"
set "PGBIN=C:\Program Files\PostgreSQL\18\bin"
if "%~1"=="" (
    echo usage: converse.cmd "your question"
    exit /b 2
)
echo SELECT * FROM laplace.converse(:'q'); | "%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -v q="%~1" -f -
