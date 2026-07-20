@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

rem One-process foundation seed: the whole ladder runs through `ingest chain`
rem in a single Laplace.Cli — one startup, one perfcache map, one native
rem runtime init, instead of one per source (seed-step.cmd pays those 12x).
rem Per-source DB verification runs after the chain, same evidence_count
rem check as seed-step :verify_step.
rem
rem usage: seed-chain.cmd [document-path]
rem   document-path defaults to %INGEST%\test-data\text

set "DOCPATH=%~1"
if "%DOCPATH%"=="" set "DOCPATH=%INGEST%\test-data\text"

cd /d "%LAPLACE_ROOT%\app"

powershell -NoProfile -Command "if (Get-CimInstance Win32_Process | Where-Object { ($_.Name -eq 'dotnet.exe' -or $_.Name -eq 'Laplace.Cli.exe') -and $_.CommandLine -match 'Laplace\.Cli' }) { exit 0 } else { exit 1 }"
if not errorlevel 1 (
  echo ERROR: a Laplace.Cli ingest is already running — wait for it to finish before seed-chain
  exit /b 2
)

set "CLI_R2R_OUT=%LAPLACE_BUILD_ROOT%\app\bin\Laplace.Cli\Release\net10.0-r2r"
set "LAPLACE_CLI_EXE=%CLI_R2R_OUT%\Laplace.Cli.exe"
if not exist "%LAPLACE_CLI_EXE%" (
  echo ERROR: ReadyToRun CLI missing: %LAPLACE_CLI_EXE% — run seed-step.cmd --rebuild unicode once to publish it
  exit /b 1
)

echo ==== seed-chain: foundation ladder in ONE process ====
"%LAPLACE_CLI_EXE%" ingest chain unicode iso639 cili semlink propbank wordnet verbnet framenet mapnet wordframenet omw atomic2020 "document %DOCPATH%"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo ERROR: seed-chain: Laplace.Cli exited %RC%
  if %RC% LSS 0 echo   ^(negative exit = NTSTATUS crash code^)
  exit /b %RC%
)

echo ==== seed-chain verify: evidence per source ====
set "FAIL=0"
for %%S in (UnicodeDecomposer ISO639Decomposer CILIDecomposer SemLinkDecomposer PropBankDecomposer WordNetDecomposer VerbNetDecomposer FrameNetDecomposer MapNetDecomposer WordFrameNetDecomposer OMWDecomposer Atomic2020Decomposer UserPrompt) do (
  set "EVID="
  for /f "usebackq delims=" %%v in (`psql -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -tAc "SELECT laplace.evidence_count(NULL, laplace.source_id('%%S'));"`) do set "EVID=%%v"
  if not defined EVID set "EVID=0"
  if "!EVID!"=="0" (
    echo ERROR: verify %%S: evidence_count=0
    set "FAIL=1"
  ) else (
    echo   verify %%S: evidence_count=!EVID!
  )
)
if "%FAIL%"=="1" exit /b 3

echo ==== SEED-CHAIN COMPLETE ====
exit /b 0
