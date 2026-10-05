@echo off
rem The engine's run-time DLLs, beside whatever loads laplace_core.dll: PostgreSQL's bin (install-extensions.cmd) and the
rem published site (publish.cmd). Windows searches a loaded module's dependencies beside the module, never on a service's
rem PATH, so every DLL in laplace_core/dynamics/synthesis's import tables (dumpbin /dependents) goes here, once, from
rem the dependency prefix and oneAPI: zlib, libxml2, ICU, TBB, MKL's TBB threading layer, Intel's libm, OpenMP and SVML.
rem   engine-runtime.cmd <destination directory>
rem A locked DLL (a running backend or worker holds it) is renamed aside and the stale image moved out of the directory.
setlocal EnableDelayedExpansion
set "DEST=%~1"
if "%DEST%"=="" ( echo engine-runtime: destination directory required & exit /b 2 )
if not defined LAPLACE_DEPS_PREFIX call "%~dp0env.cmd"
if not defined LAPLACE_ONEAPI set "LAPLACE_ONEAPI=C:\Program Files (x86)\Intel\oneAPI"
call :put "%LAPLACE_DEPS_PREFIX%\zlib\bin\z.dll" || exit /b 1
call :put "%LAPLACE_DEPS_PREFIX%\libxml2\bin\libxml2.dll" || exit /b 1
for %%F in (icuuc78 icuin78 icudt78) do call :put "%LAPLACE_DEPS_PREFIX%\icu\bin64\%%F.dll" || exit /b 1
call :put "%LAPLACE_ONEAPI%\tbb\latest\bin\tbb12.dll" || exit /b 1
call :put "%LAPLACE_ONEAPI%\tbb\latest\bin\libhwloc-15.dll"
rem MKL's TBB threading layer, whatever its current interface version (2026.1: mkl_tbb_thread.3.dll); not the debug one
set "MKL_TBB="
for %%F in ("%LAPLACE_ONEAPI%\mkl\latest\bin\mkl_tbb_thread.*.dll") do if /i not "%%~nF:~-1"=="d" set "MKL_TBB=%%~fF"
if not defined MKL_TBB ( echo missing build artifact: mkl_tbb_thread.*.dll under oneAPI mkl\latest\bin & exit /b 1 )
call :put "!MKL_TBB!" || exit /b 1
call :put "%LAPLACE_ONEAPI%\compiler\latest\bin\libmmd.dll" || exit /b 1
call :put "%LAPLACE_ONEAPI%\compiler\latest\bin\libiomp5md.dll"
call :put "%LAPLACE_ONEAPI%\compiler\latest\bin\svml_dispmd.dll" || exit /b 1
exit /b 0

:put
set "SRC=%~1"
set "BASE=%~nx1"
if not exist "%SRC%" ( echo missing build artifact: "%SRC%" & exit /b 1 )
copy /y "%SRC%" "%DEST%\%BASE%" >nul 2>nul && exit /b 0
rem Locked DLL: rename aside, then move the stale image out of the directory (a stale image left beside live
rem libraries has crashed the postmaster).
set "STALE_NAME=%BASE%.stale~%RANDOM%"
set "STALE_DIR=%TEMP%\laplace-deploy-stale"
if not exist "%STALE_DIR%" mkdir "%STALE_DIR%" >nul 2>nul
ren "%DEST%\%BASE%" "%STALE_NAME%" >nul 2>nul || ( echo cannot replace locked "%DEST%\%BASE%" & exit /b 1 )
move /y "%DEST%\%STALE_NAME%" "%STALE_DIR%\" >nul 2>nul
copy /y "%SRC%" "%DEST%\%BASE%" >nul 2>nul || ( echo cannot copy "%SRC%" to "%DEST%" & exit /b 1 )
exit /b 0
