@echo off
setlocal
call "%~dp0env.cmd"

if /i "%~1"=="--full" (
  echo ==== [publish-deploy] full native rebuild ====
  call "%~dp0build-engine.cmd" || exit /b 1
  call "%~dp0build-extensions.cmd" || exit /b 1
  call "%~dp0install-extensions.cmd" || exit /b 1
  if exist "%LAPLACE_ROOT%\external\cutechess\CMakeLists.txt" (
    echo ==== [publish-deploy] cutechess ====
    call "%~dp0build-cutechess.cmd" || echo [publish-deploy] WARN: build-cutechess failed — chess lab may be incomplete
  )
  shift
)

if /i "%~1"=="--skip-managed-build" (
  set "SKIP_MANAGED_BUILD=1"
  shift
)

call "%~dp0publish.cmd" %*
if errorlevel 1 exit /b 1
call "%~dp0deploy-api.cmd"
exit /b %ERRORLEVEL%
