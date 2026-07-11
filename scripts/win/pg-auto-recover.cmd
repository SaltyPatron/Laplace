@echo off
setlocal EnableDelayedExpansion
rem ============================================================================
rem  pg-auto-recover.cmd — unattended recovery for the ONE proven-safe failure:
rem  clean-shutdown pg_control + zeroed/unreadable checkpoint WAL segment
rem  (the Intel RST write-back signature: normal reboot, WAL tail reads zeros).
rem
rem  Acts ONLY when ALL guards hold; otherwise exits nonzero and touches nothing:
rem    G1  service %SVC% is STOPPED and nothing is listening on 5432
rem    G2  pg_controldata: "Database cluster state: shut down"  (CLEAN — a
rem        crashed/in-recovery state must never be resetwal'd unattended)
rem    G3  newest PGDATA log ends in the invalid-checkpoint PANIC
rem  Then: pg_resetwal -f (data files are consistent per G2; only the checkpoint
rem  record is unreadable), start the service, verify with SELECT 1.
rem
rem  Wire once (elevated) so failures self-heal with no human:
rem    sc failureflag postgresql-x64-18 1
rem    sc failure postgresql-x64-18 reset= 86400 command= ^
rem       "cmd /c D:\Repositories\Laplace\scripts\win\pg-auto-recover.cmd" ^
rem       actions= run/5000/run/60000/run/300000
rem  Log: D:\Data\Output\pg-auto-recover.log
rem ============================================================================
call "%~dp0env.cmd"
set "SVC=postgresql-x64-18"
set "PGDATA=%LAPLACE_PGDATA%"
set "PGBIN=C:\Program Files\PostgreSQL\18\bin"
set "LOG=D:\Data\Output\pg-auto-recover.log"

echo [%DATE% %TIME%] pg-auto-recover invoked >> "%LOG%"

rem -- G1: service stopped, port free ------------------------------------------
sc query "%SVC%" | find "STOPPED" >nul || (
  echo [%DATE% %TIME%] G1 FAIL: service not STOPPED — no action >> "%LOG%"
  exit /b 10
)
netstat -an | find ":5432 " | find "LISTENING" >nul && (
  echo [%DATE% %TIME%] G1 FAIL: 5432 listening while service stopped — orphan; use reclaim-postgres.cmd >> "%LOG%"
  exit /b 11
)

rem -- G2: pg_control reports a CLEAN shutdown ----------------------------------
"%PGBIN%\pg_controldata.exe" "%PGDATA%" | find /c "Database cluster state:               shut down" >nul
"%PGBIN%\pg_controldata.exe" "%PGDATA%" | findstr /C:"Database cluster state" | find "shut down" >nul || (
  echo [%DATE% %TIME%] G2 FAIL: cluster state is not clean 'shut down' — unattended resetwal FORBIDDEN here >> "%LOG%"
  "%PGBIN%\pg_controldata.exe" "%PGDATA%" | findstr /C:"Database cluster state" >> "%LOG%"
  exit /b 12
)

rem -- G3: newest log carries the invalid-checkpoint PANIC ----------------------
set "NEWEST="
for /f "delims=" %%F in ('dir /b /o-d "%PGDATA%\log\postgresql-*.log" 2^>nul') do (
  if not defined NEWEST set "NEWEST=%PGDATA%\log\%%F"
)
if not defined NEWEST (
  echo [%DATE% %TIME%] G3 FAIL: no postgres logs found >> "%LOG%"
  exit /b 13
)
findstr /C:"could not locate a valid checkpoint record" "%NEWEST%" >nul || (
  echo [%DATE% %TIME%] G3 FAIL: newest log (%NEWEST%) lacks the invalid-checkpoint PANIC — different failure, no action >> "%LOG%"
  exit /b 14
)

rem -- All guards hold: reset + start + verify ----------------------------------
echo [%DATE% %TIME%] guards passed (clean shutdown + invalid checkpoint) — running pg_resetwal >> "%LOG%"
"%PGBIN%\pg_resetwal.exe" -f -D "%PGDATA%" >> "%LOG%" 2>&1 || (
  echo [%DATE% %TIME%] pg_resetwal FAILED >> "%LOG%"
  exit /b 20
)
net start "%SVC%" >> "%LOG%" 2>&1 || (
  echo [%DATE% %TIME%] service start FAILED post-reset >> "%LOG%"
  exit /b 21
)
"%PGBIN%\pg_isready.exe" -h localhost -t 30 >> "%LOG%" 2>&1
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -tAc "SELECT 1" >nul 2>&1 && (
  echo [%DATE% %TIME%] RECOVERED: service up, SELECT 1 ok >> "%LOG%"
  exit /b 0
)
echo [%DATE% %TIME%] service started but SELECT 1 failed — inspect manually >> "%LOG%"
exit /b 22
