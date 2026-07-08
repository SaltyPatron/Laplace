@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

set "LOG=%INGEST%\logs\seed-everything-%DATE:~-4,4%%DATE:~-10,2%%DATE:~-7,2%-%TIME:~0,2%%TIME:~3,2%%TIME:~6,2%.log"
set "LOG=%LOG: =0%"

if not exist "%INGEST%\logs" mkdir "%INGEST%\logs"

set "LOCK=%INGEST%\logs\seed-everything.lock"
if exist "%LOCK%" (
  echo ERROR: seed-everything already running — lock=%LOCK%
  type "%LOCK%"
  exit /b 2
)
echo %DATE% %TIME% pid=%PID% > "%LOCK%"

echo ==== SEED EVERYTHING ==== > "%LOG%"
echo   STAGES: floor^(unicode iso639^) document knowledge^(cili wordnet omw verbnet propbank semlink atomic2020 mapnet wordframenet framenet ud conceptnet wiktionary^) usage^(tatoeba opensubtitles^) code^(stack repo tiny-codes^) models^(tinyllama phi qwen^) >> "%LOG%"
echo   log=%LOG% >> "%LOG%"
echo. >> "%LOG%"

rem `dotnet run` launches the CLI as dotnet.exe, so match on the command line
rem (.scratchpad/02 Issues 16/18); exit 0 = an ingest is running.
powershell -NoProfile -Command "if (Get-CimInstance Win32_Process | Where-Object { ($_.Name -eq 'dotnet.exe' -or $_.Name -eq 'Laplace.Cli.exe') -and $_.CommandLine -match 'Laplace\.Cli' }) { exit 0 } else { exit 1 }"
if not errorlevel 1 (
  echo ERROR: a Laplace.Cli ingest is already running >> "%LOG%"
  exit /b 2
)

if not defined LAPLACE_COPY_VALIDATE set "LAPLACE_COPY_VALIDATE=0"
set "LAPLACE_SKIP_USAGE=0"
set "LAPLACE_SKIP_MODELS=0"

echo ===== DB RESET ===== >> "%LOG%"
call "%~dp0db-reset.cmd" --recycle >> "%LOG%" 2>&1
if errorlevel 1 goto fail

echo ===== BUILD ===== >> "%LOG%"
dotnet build "%LAPLACE_ROOT%\app\Laplace.slnx" -c Release >> "%LOG%" 2>&1
if errorlevel 1 goto fail

echo ===== SEED LADDER (all stages, one step at a time) ===== >> "%LOG%"
call "%~dp0seed-ladder.cmd" >> "%LOG%" 2>&1
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" goto fail

echo ===== SUBSTRATE AUDIT ===== >> "%LOG%"
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -f "%LAPLACE_ROOT%\scripts\sql\substrate-audit.sql" >> "%LOG%" 2>&1
if errorlevel 1 goto fail

echo ==== SEED-EVERYTHING COMPLETE ==== >> "%LOG%"
if exist "%LOCK%" del "%LOCK%"
exit /b 0

:fail
echo ==== SEED-EVERYTHING FAILED exit=%ERRORLEVEL% ==== >> "%LOG%"
if exist "%LOCK%" del "%LOCK%"
exit /b 1
