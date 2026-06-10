@echo off
rem Laplace Windows environment chain (Intel MKL/TBB, LAPLACE_ROOT, PGBIN read-only).
rem All scripts\win\*.cmd call this first. Agent rules: .github\instructions\build-environment.instructions.md
set "NoDefaultCurrentDirectoryInExePath="
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
