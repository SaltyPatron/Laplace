@echo off
rem Laplace Windows environment chain (Intel MKL/TBB, LAPLACE_ROOT, PGBIN read-only).
rem All scripts\win\*.cmd call this first. Agent rules: .github\instructions\build-environment.instructions.md
set "NoDefaultCurrentDirectoryInExePath="
rem Same harness-pollution class: pwsh 7 exports ITS PSModulePath (Core-edition modules),
rem which breaks Windows PowerShell 5.1 module autoload in child scripts -- Get-FileHash /
rem ConvertTo-Json silently vanish. Clear it; 5.1 rebuilds its own default.
set "PSModulePath="
call "C:\Program Files (x86)\Intel\oneAPI\setvars.bat" >nul 2>&1
rem setvars adds mpi\lib and tcm\lib to LIB, but this install ships neither (TCM is runtime-only;
rem MPI here is the runtime layout) -- csc then warns CS1668 once per project. Drop dead dirs from LIB.
setlocal EnableDelayedExpansion
set "_LIB="
for %%D in ("!LIB:;=" "!") do (if not "%%~D"=="" if exist "%%~D\" set "_LIB=!_LIB!%%~D;")
endlocal & set "LIB=%_LIB%"
set "LAPLACE_ROOT=%~dp0..\.."
set "PGBIN=C:\Program Files\PostgreSQL\18\bin"
rem libxml2.dll — laplace_core links LibXml2 dynamically; gtest + dotnet test need this on PATH.
set "PATH=%PGBIN%;%PATH%"
set "PATH=C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64;%PATH%"
set "PATH=D:\Microsoft Visual Studio\2026\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja;D:\Microsoft Visual Studio\2026\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin;%PATH%"
set "PATH=C:\Program Files (x86)\Intel\oneAPI\tbb\latest\bin;C:\Program Files (x86)\Intel\oneAPI\mkl\latest\bin;C:\Program Files (x86)\Intel\oneAPI\compiler\latest\bin;%PATH%"
set "PATH=%LAPLACE_ROOT%\build-win\core;%LAPLACE_ROOT%\build-win\dynamics;%LAPLACE_ROOT%\build-win\synthesis;%PATH%"
set "LAPLACE_RC=C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/rc.exe"
set "LAPLACE_MT=C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/mt.exe"
if not defined LAPLACE_INGEST_LANGS set "LAPLACE_INGEST_LANGS=en"
rem ---- substrate constants: SINGLE SOURCE (all overridable by pre-setting) ----
rem Scripts must NOT redeclare these. To target another DB / data root for one
rem run, set the variable before calling the script.
if not defined PGPASSWORD set "PGPASSWORD=postgres"
rem LAPLACE_DBNAME = bare database name (psql/createdb); LAPLACE_DB composes from it.
rem Retargeting everything at another DB is one variable: set LAPLACE_DBNAME=laplace_export
if not defined LAPLACE_DBNAME set "LAPLACE_DBNAME=laplace"
if not defined LAPLACE_DB set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=%LAPLACE_DBNAME%"
if not defined LAPLACE_PERFCACHE_BIN set "LAPLACE_PERFCACHE_BIN=%LAPLACE_ROOT%\build-win\core\perfcache\laplace_t0_perfcache.bin"
if not defined INGEST set "INGEST=D:\Data\Ingest"
if not defined REPOS set "REPOS=D:\Repositories"
if not defined LAPLACE_MODEL_HUB set "LAPLACE_MODEL_HUB=D:\Models\hub"
