@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

set "SRC=%LAPLACE_PUBLISH_ENDPOINT%"
set "LIVE=%LAPLACE_IIS_API%"
set "APPCMD=%SystemRoot%\System32\inetsrv\appcmd.exe"
set "POOL=LaplacePool"
set "SITE=Laplace"

if not exist "%SRC%\Laplace.Endpoints.OpenAICompat.dll" (
  echo [deploy-api] ERROR: no staged build at %SRC% — run scripts\win\publish.cmd first
  exit /b 1
)
if not exist "%LIVE%\" (
  echo [deploy-api] ERROR: live folder %LIVE% not found
  exit /b 1
)

if not exist "%APPCMD%" (
  echo [deploy-api] ERROR: appcmd.exe not found — cannot manage IIS app pool
  exit /b 1
)

echo ==== stop IIS app pool %POOL% ====
rem appcmd stop errors on an already-stopped pool — read the state first.
rem for /f + quoted Program Files path eats quotes; stage state to a temp file.
set "POOL_STATE="
set "POOL_STATE_FILE=%TEMP%\laplace-iis-pool-state.txt"
"%APPCMD%" list apppool "%POOL%" /text:state > "%POOL_STATE_FILE%" 2>nul
if exist "%POOL_STATE_FILE%" set /p POOL_STATE=<"%POOL_STATE_FILE%"
if /i "!POOL_STATE!"=="Stopped" (
  echo [deploy-api] pool %POOL% already stopped
) else (
  "%APPCMD%" stop apppool /apppool.name:%POOL%
  if errorlevel 1 (
    echo [deploy-api] ERROR: failed to stop %POOL% — run elevated if needed
    exit /b 1
  )
)
set /a "WAIT=0"
:wait_pool_stop
"%APPCMD%" list wp 2>nul | "%SystemRoot%\System32\find.exe" /i "LaplacePool" >nul
if not errorlevel 1 (
  set /a "WAIT+=1"
  if !WAIT! geq 90 (
    echo [deploy-api] ERROR: LaplacePool worker still running after 90s — aborting copy
    goto pool_start_fail
  )
  timeout /t 1 /nobreak >nul 2>nul
  goto wait_pool_stop
)

echo ==== mirror staged endpoint -^> %LIVE% ^(incl. web.config; keep logs^) ====
robocopy "%SRC%" "%LIVE%" /MIR /XD logs /R:2 /W:2 /NFL /NDL /NJH /NJS /NP
if errorlevel 8 (
  echo [deploy-api] robocopy endpoint FAILED
  goto pool_start_fail
)

echo ==== verify staged binaries landed in IIS folder ====
set "VERIFY_FAILED=0"
for %%F in (
  Laplace.Endpoints.OpenAICompat.dll
  Laplace.Core.dll
  Laplace.Chess.dll
  Laplace.Substrate.dll
  laplace-uci.exe
  laplace_core.dll
  laplace_dynamics.dll
  laplace_synthesis.dll
) do (
  call :verify_hash "%%F" || set "VERIFY_FAILED=1"
)
if not "!VERIFY_FAILED!"=="0" goto pool_start_fail

echo ==== start IIS app pool %POOL% ====
set "POOL_STATE="
"%APPCMD%" list apppool "%POOL%" /text:state > "%POOL_STATE_FILE%" 2>nul
if exist "%POOL_STATE_FILE%" set /p POOL_STATE=<"%POOL_STATE_FILE%"
if /i "!POOL_STATE!"=="Started" (
  echo [deploy-api] pool %POOL% already started
) else (
  "%APPCMD%" start apppool /apppool.name:%POOL%
  if errorlevel 1 (
    echo [deploy-api] ERROR: failed to start %POOL%
    exit /b 1
  )
)
set "SITE_STATE="
set "SITE_STATE_FILE=%TEMP%\laplace-iis-site-state.txt"
"%APPCMD%" list site "%SITE%" /text:state > "%SITE_STATE_FILE%" 2>nul
if exist "%SITE_STATE_FILE%" set /p SITE_STATE=<"%SITE_STATE_FILE%"
if /i "!SITE_STATE!"=="Started" (
  echo [deploy-api] site %SITE% already started
) else (
  "%APPCMD%" start site /site.name:%SITE%
  if errorlevel 1 (
    echo [deploy-api] ERROR: failed to start site %SITE%
    exit /b 1
  )
)

echo ==== wait for /health/ready ====
set /a "READY_WAIT=0"
:wait_ready
powershell -NoProfile -Command ^
  "try { $r = Invoke-RestMethod 'http://localhost:8080/health/ready' -TimeoutSec 5; if ($r.ready) { exit 0 }; Write-Host ('not ready: ' + $r.detail); exit 2 } catch { Write-Host $_.Exception.Message; exit 1 }"
set "READY_RC=!ERRORLEVEL!"
if "!READY_RC!"=="0" goto ready_ok
set /a "READY_WAIT+=1"
if !READY_WAIT! geq 30 (
  echo [deploy-api] ERROR: /health/ready did not become ready within 30s
  exit /b 1
)
timeout /t 1 /nobreak >nul 2>nul
goto wait_ready

:ready_ok
echo.
echo [deploy-api] OK — %LIVE% mirrored from %SRC% and /health/ready is ready
exit /b 0

:verify_hash
set "NAME=%~1"
if not exist "%SRC%\!NAME!" (
  echo [deploy-api] ERROR: staged file missing: %SRC%\!NAME!
  exit /b 1
)
if not exist "%LIVE%\!NAME!" (
  echo [deploy-api] ERROR: live file missing after copy: %LIVE%\!NAME!
  exit /b 1
)
for /f "skip=1 tokens=*" %%H in ('certutil -hashfile "%SRC%\!NAME!" SHA256 ^| findstr /v /i certutil') do set "SRC_HASH=%%H"
for /f "skip=1 tokens=*" %%H in ('certutil -hashfile "%LIVE%\!NAME!" SHA256 ^| findstr /v /i certutil') do set "LIVE_HASH=%%H"
set "SRC_HASH=!SRC_HASH: =!"
set "LIVE_HASH=!LIVE_HASH: =!"
if /i not "!SRC_HASH!"=="!LIVE_HASH!" (
  echo [deploy-api] ERROR: hash mismatch for !NAME!
  exit /b 1
)
echo   verified !NAME!
exit /b 0

:pool_start_fail
echo ==== restart IIS app pool %POOL% after failure ====
"%APPCMD%" start apppool /apppool.name:%POOL% >nul 2>&1
exit /b 1
