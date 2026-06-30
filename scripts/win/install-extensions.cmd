@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
set "DEPLOY=D:\Data\Postgres\laplace"
set "RECYCLE=0"
if /i "%~1"=="--recycle" set "RECYCLE=1"
if not exist "%DEPLOY%\lib" mkdir "%DEPLOY%\lib"
if not exist "%DEPLOY%\share\extension" mkdir "%DEPLOY%\share\extension"
del /q "%DEPLOY%\lib\*.stale~*" 2>nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gen-sql.ps1" -Stage "%DEPLOY%\share_stage" || exit /b 1
move /y "%DEPLOY%\share_stage\extension\*" "%DEPLOY%\share\extension\" >nul
rmdir /s /q "%DEPLOY%\share_stage" 2>nul
call :swapcopy "build-win-ext\laplace_geom\laplace_geom.dll" || exit /b 1
call :swapcopy "build-win-ext\laplace_substrate\laplace_substrate.dll" || exit /b 1
call :swapcopy "build-win\core\laplace_core.dll" || exit /b 1
call :swapcopy "build-win\dynamics\laplace_dynamics.dll" || exit /b 1
copy /y "build-win\core\perfcache\laplace_t0_perfcache.bin" "%DEPLOY%\share\laplace_t0_perfcache.bin" >nul || exit /b 1
copy /y "build-win\core\perfcache\laplace_highway_perfcache.bin" "%DEPLOY%\share\laplace_highway_perfcache.bin" >nul || exit /b 1
call :swapcopy "C:\Program Files (x86)\Intel\oneAPI\tbb\latest\bin\tbb12.dll" || exit /b 1
call :swapcopy "C:\Program Files (x86)\Intel\oneAPI\tbb\latest\bin\libhwloc-15.dll"
echo deployed: %DEPLOY%
set "PSQL=%PGBIN%\psql.exe"
"%PSQL%" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "ALTER SYSTEM SET extension_control_path = 'D:/Data/Postgres/laplace/share;$system';" || exit /b 1
"%PSQL%" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "ALTER SYSTEM SET dynamic_library_path = '$libdir;D:/Data/Postgres/laplace/lib';" || exit /b 1
"%PSQL%" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "ALTER SYSTEM SET laplace_substrate.perfcache_path = 'D:/Data/Postgres/laplace/share/laplace_t0_perfcache.bin';" || exit /b 1
"%PSQL%" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_reload_conf();" || exit /b 1
"%PSQL%" -h localhost -U postgres -d postgres -tAc "SELECT name, default_version FROM pg_available_extensions WHERE name LIKE 'laplace%%' ORDER BY 1;"
if "%RECYCLE%"=="1" (
  echo recycling laplace backends so fresh DLLs/SQL load on next connection...
  "%PSQL%" -h localhost -U postgres -d postgres -tAc "SELECT count(pg_terminate_backend(pid)) || ' backend(s) recycled' FROM pg_stat_activity WHERE datname LIKE 'laplace%%' AND pid <> pg_backend_pid();"
)
echo wired: extension_control_path + dynamic_library_path now include %DEPLOY%
exit /b 0

:swapcopy
set "SRC=%~1"
set "BASE=%~nx1"
if exist "%SRC%" goto sc_copy
echo missing build artifact: "%SRC%"
exit /b 1
:sc_copy
copy /y "%SRC%" "%DEPLOY%\lib\%BASE%" >nul 2>nul && exit /b 0
ren "%DEPLOY%\lib\%BASE%" "%BASE%.stale~%RANDOM%" 2>nul || goto sc_fail
copy /y "%SRC%" "%DEPLOY%\lib\%BASE%" >nul 2>nul || goto sc_fail
echo   hot-swapped %BASE% -- old image renamed; backends pick up the new copy on reconnect
exit /b 0
:sc_fail
echo FAILED to deploy %BASE% into %DEPLOY%\lib
exit /b 1
