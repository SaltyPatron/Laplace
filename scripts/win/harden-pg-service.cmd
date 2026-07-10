@echo off
setlocal EnableDelayedExpansion
rem ============================================================================
rem  harden-pg-service.cmd — permanent SCM/orphan detach mitigations
rem
rem  RUN FROM ELEVATED CMD (Administrator):
rem    cmd /c "scripts\win\harden-pg-service.cmd"
rem
rem  What this changes (cluster data untouched):
rem    1. PGCTLTIMEOUT=600 on postgresql-x64-18 so long crash-recovery
rem       does not time out pg_ctl -w and leave an orphan postmaster
rem       while the service flips to STOPPED.
rem    2. ServicesPipeTimeout >= 600000 ms (10 min) so SCM waits long
rem       enough for the same recovery window.
rem    3. Deletes deploy *.stale~* hot-swap leftovers (crash poison).
rem    4. Refuses to leave you on an orphan: if STOPPED+5432 live,
rem       points at reclaim-postgres.cmd (does not kill from here).
rem
rem  Does NOT drop databases. Does NOT run pg_ctl start (that creates orphans).
rem ============================================================================
call "%~dp0env.cmd"
set "SVC=postgresql-x64-18"
set "SVC_KEY=HKLM\SYSTEM\CurrentControlSet\Services\%SVC%"
set "CTRL_KEY=HKLM\SYSTEM\CurrentControlSet\Control"

net session >nul 2>&1
if errorlevel 1 (
  echo ERROR: harden-pg-service requires Administrator.
  echo   Right-click cmd ^> Run as administrator, then:
  echo   cmd /c "scripts\win\harden-pg-service.cmd"
  exit /b 1
)

echo ==== [1/4] PGCTLTIMEOUT=600 on %SVC% ====
reg add "%SVC_KEY%" /v Environment /t REG_MULTI_SZ /d "PGCTLTIMEOUT=600" /f >nul
if errorlevel 1 (
  echo ERROR: failed to set service Environment PGCTLTIMEOUT
  exit /b 1
)
reg query "%SVC_KEY%" /v Environment

echo ==== [2/4] ServicesPipeTimeout ^>= 600000 ms ====
set "NEED_PIPE=0"
for /f "tokens=3" %%V in ('reg query "%CTRL_KEY%" /v ServicesPipeTimeout 2^>nul ^| findstr /i ServicesPipeTimeout') do set "CUR_PIPE=%%V"
if not defined CUR_PIPE set "NEED_PIPE=1"
if defined CUR_PIPE (
  rem CUR_PIPE is hex like 0x493e0 — compare via powershell
  powershell -NoProfile -Command ^
    "$v=[Convert]::ToInt32('%CUR_PIPE%', 16); if ($v -lt 600000) { exit 9 } else { exit 0 }"
  if errorlevel 9 set "NEED_PIPE=1"
)
if "%NEED_PIPE%"=="1" (
  reg add "%CTRL_KEY%" /v ServicesPipeTimeout /t REG_DWORD /d 600000 /f >nul
  echo ServicesPipeTimeout set to 600000
) else (
  echo ServicesPipeTimeout already sufficient ^(%CUR_PIPE%^)
)

echo ==== [3/4] clear deploy stale~ leftovers ====
del /q "%LAPLACE_DEPLOY%\lib\*.stale~*" 2>nul
del /q "%LAPLACE_DEPLOY%\share\*.stale~*" 2>nul
if not exist "%LAPLACE_DATA_ROOT%\stale-trash" mkdir "%LAPLACE_DATA_ROOT%\stale-trash" 2>nul
echo stale~ cleared; trash dir %LAPLACE_DATA_ROOT%\stale-trash

echo ==== [4/4] orphan / SCM check ====
sc query %SVC% | findstr /i "STATE"
set "PORT_OWNER="
for /f "tokens=5" %%P in ('netstat -ano ^| findstr /r /c:":5432 .*LISTENING"') do set "PORT_OWNER=%%P"
sc query %SVC% | findstr /i "RUNNING" >nul
if errorlevel 1 (
  if defined PORT_OWNER (
    echo BROKEN: service STOPPED but PID !PORT_OWNER! holds 5432 — orphan detach.
    echo   Fix: cmd /c "scripts\win\reclaim-postgres.cmd"
    exit /b 2
  )
  echo service not RUNNING and 5432 free — start via SCM only:
  echo   net start %SVC%
  echo NEVER: pg_ctl start  ^(creates orphans NetworkService cannot reclaim unelevated^)
  exit /b 0
)
if not defined PORT_OWNER (
  echo WARN: service RUNNING but 5432 not listening — check %LAPLACE_PGDATA%\log\
  exit /b 3
)
echo OK: %SVC% RUNNING, port 5432 owned by PID !PORT_OWNER!, timeouts hardened.
echo NOTE: service Environment changes apply on next service start.
exit /b 0
