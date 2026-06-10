@echo off
setlocal
rem Deploy extensions to D:\Data\Postgres\laplace and wire PG GUCs. NEVER copy into C:\Program Files\PostgreSQL.
rem Agent rules: .github\instructions\build-environment.instructions.md
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
set "DEPLOY=D:\Data\Postgres\laplace"
if not exist "%DEPLOY%\lib" mkdir "%DEPLOY%\lib"
if not exist "%DEPLOY%\share\extension" mkdir "%DEPLOY%\share\extension"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gen-sql.ps1" -Stage "%DEPLOY%\share_stage" || exit /b 1
move /y "%DEPLOY%\share_stage\extension\*" "%DEPLOY%\share\extension\" >nul
rmdir /s /q "%DEPLOY%\share_stage" 2>nul
copy /y build-win-ext\laplace_geom\laplace_geom.dll "%DEPLOY%\lib\" >nul || exit /b 1
copy /y build-win-ext\laplace_substrate\laplace_substrate.dll "%DEPLOY%\lib\" >nul || exit /b 1
copy /y build-win\core\laplace_core.dll "%DEPLOY%\lib\" >nul || exit /b 1
copy /y build-win\dynamics\laplace_dynamics.dll "%DEPLOY%\lib\" >nul || exit /b 1
copy /y "C:\Program Files (x86)\Intel\oneAPI\tbb\latest\bin\tbb12.dll" "%DEPLOY%\lib\" >nul || exit /b 1
copy /y "C:\Program Files (x86)\Intel\oneAPI\tbb\latest\bin\libhwloc-15.dll" "%DEPLOY%\lib\" >nul
echo deployed: %DEPLOY%
set "PGPASSWORD=postgres"
set "PSQL=C:\Program Files\PostgreSQL\18\bin\psql.exe"
"%PSQL%" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "ALTER SYSTEM SET extension_control_path = '$system;D:/Data/Postgres/laplace/share';" || exit /b 1
"%PSQL%" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "ALTER SYSTEM SET dynamic_library_path = '$libdir;D:/Data/Postgres/laplace/lib';" || exit /b 1
"%PSQL%" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_reload_conf();" || exit /b 1
"%PSQL%" -h localhost -U postgres -d postgres -tAc "SELECT name, default_version FROM pg_available_extensions WHERE name LIKE 'laplace%%' ORDER BY 1;"
echo wired: extension_control_path + dynamic_library_path now include %DEPLOY%
