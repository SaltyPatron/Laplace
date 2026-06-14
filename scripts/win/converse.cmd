@echo off
setlocal
call "%~dp0env.cmd"
if "%~1"=="" (
    echo usage: converse.cmd "your question"
    exit /b 2
)
echo SELECT * FROM laplace.recall(:'q'); | "%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -v q="%~1" -f -
