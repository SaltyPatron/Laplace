@echo off
setlocal
if "%~2"=="" (
    echo usage: refresh-substrate-module.cmd ^<path/to/object.sql.in^> ^<database^>
    echo example: refresh-substrate-module.cmd functions/views/v_entities_highway.sql.in laplace
    echo.
    echo Rebuilds and deploys the full extension (PostGIS model). For one object, edit manifest.install
    echo and run: build-extensions.cmd --reconfigure ^&^& install-extensions.cmd --recycle
    exit /b 2
)
call "%~dp0env.cmd"
echo refresh-substrate-module: per-object hot refresh removed — use install-extensions.cmd after rebuild.
exit /b 1
