@echo off





setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

set "CONFIRMED=0"
set "SOURCE="
set "DO_COLD=1"
set "DO_WARM=1"
set "DO_ISOLATE=1"
set "DO_BUILD=1"
set "DO_DRY=0"
set "DO_FULL=0"
set "BENCH_PATH="
set "BENCH_DB=laplace_bench"
set "MAX_UNITS=50000"

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--confirm" (set "CONFIRMED=1" & shift & goto parse_args)
if /i "%~1"=="--dry-run" (set "DO_DRY=1" & shift & goto parse_args)
if /i "%~1"=="--full" (set "DO_FULL=1" & shift & goto parse_args)
if /i "%~1"=="--max-units" (set "MAX_UNITS=%~2" & shift & shift & goto parse_args)
if /i "%~1"=="--cold-only" (set "DO_WARM=0" & shift & goto parse_args)
if /i "%~1"=="--warm-only" (set "DO_COLD=0" & set "DO_ISOLATE=0" & shift & goto parse_args)
if /i "%~1"=="--no-isolate" (set "DO_ISOLATE=0" & shift & goto parse_args)
if /i "%~1"=="--no-build" (set "DO_BUILD=0" & shift & goto parse_args)
if /i "%~1"=="--path" (set "BENCH_PATH=%~2" & shift & shift & goto parse_args)
if /i "%~1"=="--db" (set "BENCH_DB=%~2" & shift & shift & goto parse_args)
if not defined SOURCE (
  if /i "%~1"=="conceptnet" (set "SOURCE=conceptnet" & shift & goto parse_args)
  if /i "%~1"=="wiktionary" (set "SOURCE=wiktionary" & shift & goto parse_args)
  if /i "%~1"=="wiktionary-en" (set "SOURCE=wiktionary-en" & shift & goto parse_args)
  if /i "%~1"=="ud" (set "SOURCE=ud" & shift & goto parse_args)
)
echo ERROR: unknown or misplaced argument %~1
exit /b 2

:args_done
if "%CONFIRMED%"=="0" (
  echo bench-ingest: refused — pass --confirm and a source explicitly.
  echo   Example: bench-ingest.cmd --confirm wiktionary-en
  exit /b 2
)
if not defined SOURCE (
  echo ERROR: source required: conceptnet ^| wiktionary ^| wiktionary-en ^| ud
  exit /b 2
)
if /i "%BENCH_DB%"=="laplace" (
  echo ERROR: refusing to benchmark against production database laplace
  exit /b 2
)

if not defined LAPLACE_EMIT_CROSS_LANG set "LAPLACE_EMIT_CROSS_LANG=0"
if not defined LAPLACE_COPY_VALIDATE set "LAPLACE_COPY_VALIDATE=0"
if not defined LAPLACE_APPLY_PARTITIONS set "LAPLACE_APPLY_PARTITIONS=1"
if "%DO_FULL%"=="1" (set "MAX_UNITS=0") else (set "LAPLACE_INGEST_MAX_UNITS=%MAX_UNITS%")

call :configure_source "%SOURCE%"
if errorlevel 1 exit /b 1

set "CLI_EXE=%LAPLACE_CLI_EXE%"
for /f "delims=" %%t in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "STAMP=%%t"
set "RUN_DIR=%LAPLACE_ROOT%\docs\bench\runs\%STAMP%_%SOURCE%"
mkdir "%RUN_DIR%" 2>nul

echo ============================================================
echo  LAPLACE INGEST BENCHMARK source=%SOURCE% db=%BENCH_DB% max_units=%MAX_UNITS%
echo ============================================================

if "%DO_BUILD%"=="1" (
  echo ==== build Release app ====
  cd /d "%LAPLACE_ROOT%\app"
  dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release > "%RUN_DIR%\build.log" 2>&1
  if errorlevel 1 exit /b 1
)

cd /d "%LAPLACE_ROOT%"
if "%DO_ISOLATE%"=="1" if "%DO_COLD%"=="1" (
  echo ==== fresh DB: %BENCH_DB% ====
  call "%LAPLACE_ROOT%\scripts\win\db-isolate.cmd" "%BENCH_DB%" || exit /b 1
)

set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=%BENCH_DB%"
set "LAPLACE_DBNAME=%BENCH_DB%"
call :print_env > "%RUN_DIR%\env.txt"
type "%RUN_DIR%\env.txt"

if "%DO_DRY%"=="1" (
  echo DRY-RUN: would write logs to %RUN_DIR%
  exit /b 0
)

call :measure_input > "%RUN_DIR%\input.txt"
type "%RUN_DIR%\input.txt"

if "%DO_COLD%"=="1" (
  echo ==== COLD RUN ====
  call :run_ingest cold "%RUN_DIR%\cold.log" ""
  if errorlevel 1 exit /b 1
  call :parse_log "%RUN_DIR%\cold.log" > "%RUN_DIR%\cold.metrics"
  type "%RUN_DIR%\cold.metrics"
)

if "%DO_WARM%"=="1" (
  echo ==== WARM RUN ====
  call :run_ingest warm "%RUN_DIR%\warm.log" "--force"
  if errorlevel 1 exit /b 1
  call :parse_log "%RUN_DIR%\warm.log" > "%RUN_DIR%\warm.metrics"
  type "%RUN_DIR%\warm.metrics"
)

echo run_dir=%RUN_DIR%
exit /b 0

:configure_source
set "INGEST_STEP=%~1"
set "INGEST_LANGS="
set "INGEST_CLI_SOURCE="
if /i "%INGEST_STEP%"=="conceptnet" (
  if not defined LAPLACE_INGEST_COMMIT_ROWS set "LAPLACE_INGEST_COMMIT_ROWS=50000"
  if not defined LAPLACE_INGEST_BATCH set "LAPLACE_INGEST_BATCH=16384"
  if not defined LAPLACE_INGEST_WORKERS set "LAPLACE_INGEST_WORKERS=8"
  if not defined LAPLACE_COMMIT_LANES set "LAPLACE_COMMIT_LANES=8"
  if not defined BENCH_PATH set "BENCH_PATH=%INGEST%\ConceptNet"
)
if /i "%INGEST_STEP%"=="wiktionary" (
  if not defined LAPLACE_INGEST_COMMIT_ROWS set "LAPLACE_INGEST_COMMIT_ROWS=50000"
  if not defined LAPLACE_INGEST_BATCH set "LAPLACE_INGEST_BATCH=8192"
  if not defined BENCH_PATH set "BENCH_PATH=%INGEST%\Wiktionary"
)
if /i "%INGEST_STEP%"=="wiktionary-en" (
  if not defined LAPLACE_INGEST_COMMIT_ROWS set "LAPLACE_INGEST_COMMIT_ROWS=50000"
  if not defined LAPLACE_INGEST_BATCH set "LAPLACE_INGEST_BATCH=8192"
  set "INGEST_LANGS=--langs en"
  set "INGEST_CLI_SOURCE=wiktionary"
  if not defined BENCH_PATH set "BENCH_PATH=%INGEST%\Wiktionary"
)
if not defined INGEST_CLI_SOURCE set "INGEST_CLI_SOURCE=%INGEST_STEP%"
if /i "%INGEST_STEP%"=="ud" (
  if not defined LAPLACE_INGEST_COMMIT_ROWS set "LAPLACE_INGEST_COMMIT_ROWS=25000"
  if not defined BENCH_PATH set "BENCH_PATH=%INGEST%\UD-Treebanks"
)
exit /b 0

:print_env
echo host=%COMPUTERNAME%
echo LAPLACE_DB=%LAPLACE_DB%
echo LAPLACE_INGEST_MAX_UNITS=%LAPLACE_INGEST_MAX_UNITS%
echo BENCH_PATH=%BENCH_PATH%
echo CLI_EXE=%CLI_EXE%
exit /b 0

:measure_input
powershell -NoProfile -ExecutionPolicy Bypass -File "%LAPLACE_ROOT%\scripts\win\bench-ingest-measure.ps1" -Source "%SOURCE%" -Path "%BENCH_PATH%"
exit /b 0

:run_ingest
set "LOG_FILE=%~2"
set "EXTRA_FLAGS=%~3"
cd /d "%LAPLACE_ROOT%"
echo command: "%CLI_EXE%" ingest %INGEST_CLI_SOURCE% "%BENCH_PATH%" %INGEST_LANGS% %EXTRA_FLAGS% > "%LOG_FILE%.cmd"
powershell -NoProfile -Command "$sw=[Diagnostics.Stopwatch]::StartNew(); & '%CLI_EXE%' ingest %INGEST_CLI_SOURCE% '%BENCH_PATH%' %INGEST_LANGS% %EXTRA_FLAGS% 2>&1 | Tee-Object -FilePath '%LOG_FILE%' -Append; $sw.Stop(); 'WALL_CLOCK_SEC=' + [math]::Round($sw.Elapsed.TotalSeconds,2) | Tee-Object -FilePath '%LOG_FILE%' -Append"
exit /b %ERRORLEVEL%

:parse_log
powershell -NoProfile -ExecutionPolicy Bypass -File "%LAPLACE_ROOT%\scripts\win\bench-ingest-parse.ps1" -LogPath "%~1" -InputFile "%RUN_DIR%\input.txt"
exit /b 0