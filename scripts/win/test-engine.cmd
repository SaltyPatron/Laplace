@echo off

setlocal EnableDelayedExpansion






call "%~dp0env.cmd"

cd /d "%LAPLACE_ROOT%"



set "_extra="

set "_regex="

if defined LAPLACE_CTEST_REGEX set "_regex=!LAPLACE_CTEST_REGEX!"



:arg_loop

if "%~1"=="" goto arg_run

if /i "%~1"=="-R" (

  set "_regex=%~2"

  shift

  shift

  goto arg_loop

)

set "_extra=!_extra! %~1"

shift

goto arg_loop



:arg_run

rem CTEST_PARALLEL_LEVEL from env.cmd; -j on the command line overrides it.
rem LAPLACE_TEST_SERIAL=1 forces -j1 when no explicit -j was passed.
set "_jflag="
echo !_extra! | findstr /i /c:" -j" /c:"-j " >nul && set "_jflag=1"
if not defined _jflag if defined LAPLACE_TEST_SERIAL set "_extra=!_extra! -j1"
if not defined _jflag if not defined LAPLACE_TEST_SERIAL if defined CTEST_PARALLEL_LEVEL set "_extra=!_extra! -j !CTEST_PARALLEL_LEVEL!"

if defined _regex (

  ctest --test-dir "%LAPLACE_ENGINE_BUILD%" --output-on-failure !_extra! -R "!_regex!"

) else (

  ctest --test-dir "%LAPLACE_ENGINE_BUILD%" --output-on-failure !_extra!

)

exit /b !ERRORLEVEL!

