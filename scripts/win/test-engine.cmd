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

if defined _regex (

  ctest --test-dir build-win --output-on-failure !_extra! -R "!_regex!"

) else (

  ctest --test-dir build-win --output-on-failure !_extra!

)

exit /b !ERRORLEVEL!

