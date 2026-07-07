@echo off
setlocal
call "%~dp0env.cmd"
set "DEPLOY=%LAPLACE_DEPLOY%"

echo ==== prep-postgres-start ====
echo PGDATA cluster files are NOT deleted by this script.
echo This only refreshes %DEPLOY% and clears hot-swap leftovers that block startup fsync.
echo.

call "%~dp0bootstrap-deploy.cmd" || exit /b 1

del /q "%DEPLOY%\lib\*.stale~*" 2>nul
del /q "%DEPLOY%\share\*.stale~*" 2>nul

echo.
echo deploy tree ready. Next:
echo   1. Start postgresql-x64-18 in services.msc
echo   2. If it stays up: scripts\win\install-extensions.cmd  ^(sync GUCs^)
echo   3. Restart postgresql-x64-18  ^(shared_preload_libraries needs postmaster restart^)
echo   4. scripts\win\verify-deploy.cmd
exit /b 0
