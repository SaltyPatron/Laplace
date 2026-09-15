@echo off
setlocal
rem Capture script dir BEFORE any shift — bare "shift" rotates %%0, so %%~dp0
rem would resolve against cwd (repo root) and call the wrong publish.cmd.
set "HERE=%~dp0"
call "%HERE%env.cmd"

set "DO_FULL=0"
set "PUBLISH_ARGS="

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--full" (
  set "DO_FULL=1"
  shift /1
  goto parse_args
)
if /i "%~1"=="--skip-managed-build" (
  set "PUBLISH_ARGS=--skip-managed-build"
  shift /1
  goto parse_args
)
echo [publish-deploy] unknown argument %~1
exit /b 2
:args_done

echo ==== [publish-deploy] billing secrets + Stripe listen ====
call "%HERE%ensure-billing-runtime.cmd"
if errorlevel 1 (
  echo [publish-deploy] WARN: billing runtime incomplete — continuing publish
)

if "%DO_FULL%"=="1" (
  echo ==== [publish-deploy] full native rebuild ====
  call "%HERE%build-engine.cmd" || exit /b 1
  call "%HERE%build-extensions.cmd" || exit /b 1
  call "%HERE%install-extensions.cmd" || exit /b 1

)

rem Fresh and incremental publishes must both supply working chess dependencies.
call "%HERE%build-cutechess.cmd" || exit /b 1
call "%HERE%ensure-stockfish-toolchain.cmd" || exit /b 1
python "%LAPLACE_ROOT%\scripts\install-stockfish.py" || exit /b 1

call "%HERE%publish.cmd" %PUBLISH_ARGS%
if errorlevel 1 exit /b 1
call "%HERE%deploy-api.cmd"
exit /b %ERRORLEVEL%
