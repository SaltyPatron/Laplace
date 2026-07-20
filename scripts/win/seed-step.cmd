@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

if "%~1"=="" goto usage
if /i "%~1"=="help" goto usage
if /i "%~1"=="--list" goto list

set "REBUILD=0"
:parse_flags
if /i "%~1"=="--rebuild" ( set "REBUILD=1" & shift /1 & goto parse_flags )
if "%~1"=="" goto usage

set "STEP=%~1"
set "EXTRA="
if not "%~2"=="" set "EXTRA=%~2"
if not "%~3"=="" set "EXTRA=%EXTRA% %~3"
if not "%~4"=="" set "EXTRA=%EXTRA% %~4"
if not "%~5"=="" set "EXTRA=%EXTRA% %~5"
if not "%~6"=="" set "EXTRA=%EXTRA% %~6"
if not "%~7"=="" set "EXTRA=%EXTRA% %~7"
if not "%~8"=="" set "EXTRA=%EXTRA% %~8"
if not "%~9"=="" set "EXTRA=%EXTRA% %~9"

rem wiktionary corpus selector (2nd positional): "english"/"en" -> English-only
rem kaikki.org-dictionary-English.jsonl (~2.9 GB, --langs en activates the filter
rem so WiktionaryDecomposer.ResolveInput picks it); "all"/"multi" -> the full
rem multilingual raw-wiktextract-data.jsonl (~21.9 GB, no filter). Anything else
rem (e.g. a literal "--langs de,fr") passes straight through unchanged.
if /i "%STEP%"=="wiktionary" (
  if /i "%~2"=="english" set "EXTRA=--langs en"
  if /i "%~2"=="en"      set "EXTRA=--langs en"
  if /i "%~2"=="all"     set "EXTRA="
  if /i "%~2"=="multi"   set "EXTRA="
)

if not defined LAPLACE_EMIT_CROSS_LANG set "LAPLACE_EMIT_CROSS_LANG=0"




if not defined LAPLACE_COPY_VALIDATE set "LAPLACE_COPY_VALIDATE=0"

if /i not "%STEP%"=="unicode" set "LAPLACE_INGEST_MAX_UNITS="

cd /d "%LAPLACE_ROOT%\app"

if /i "%STEP%"=="unicode"       goto run_ingest
if /i "%STEP%"=="iso639"        goto run_ingest
if /i "%STEP%"=="cili"          goto run_ingest
if /i "%STEP%"=="wordnet"       goto run_ingest
if /i "%STEP%"=="omw"           goto run_ingest
if /i "%STEP%"=="verbnet"       goto run_ingest
if /i "%STEP%"=="propbank"      goto run_ingest
if /i "%STEP%"=="framenet"      goto run_ingest
if /i "%STEP%"=="semlink"       goto run_ingest
if /i "%STEP%"=="mapnet"        goto run_ingest
if /i "%STEP%"=="wordframenet" goto run_ingest
if /i "%STEP%"=="conceptnet"    goto run_ingest
if /i "%STEP%"=="atomic2020"    goto run_ingest
if /i "%STEP%"=="ud"            goto run_ingest
if /i "%STEP%"=="wiktionary"    goto run_ingest
if /i "%STEP%"=="tatoeba"       goto run_ingest
if /i "%STEP%"=="opensubtitles" goto run_ingest_opensubtitles_commit
if /i "%STEP%"=="document"      goto run_ingest_path
if /i "%STEP%"=="chess"         goto run_ingest_path
if /i "%STEP%"=="chess-books"   goto run_ingest_path
if /i "%STEP%"=="openings"      goto run_ingest_path
if /i "%STEP%"=="stack"         goto run_ingest_path
if /i "%STEP%"=="repo"          goto run_ingest_path
if /i "%STEP%"=="tiny-codes"    goto run_ingest_path
if /i "%STEP%"=="model-tinyllama" goto run_model_tinyllama
if /i "%STEP%"=="model-phi"      goto run_model_phi
if /i "%STEP%"=="model-qwen"     goto run_model_qwen
if /i "%STEP%"=="safetensors"    goto run_ingest_path

echo ERROR: unknown seed step '%STEP%'
goto unknown_step

:run_ingest
rem Batch/commit/working-set sizing is source-aware in IngestSizing (CLI/decomposer).
rem Do not override LAPLACE_INGEST_* here except LAPLACE_INGEST_MAX_UNITS smoke caps.
call :run_ingest_impl
exit /b %ERRORLEVEL%

:run_ingest_opensubtitles_commit
call :run_ingest_impl
exit /b %ERRORLEVEL%

:run_ingest_path
if "%EXTRA%"=="" (
  echo ERROR: seed-step %STEP% requires a path argument
  exit /b 2
)
call :run_ingest_impl
exit /b %ERRORLEVEL%

:run_ingest_impl
rem Match on the command line: any dotnet.exe/Laplace.Cli.exe process whose
rem command line mentions Laplace.Cli is a live ingest (idle build-server
rem dotnet processes don't mention it). Prefer the built CLI exe so the mutex
rem matches Laplace.Cli.exe directly (.scratchpad/02 Issues 16/18).
call :cli_running
if not errorlevel 1 (
  echo ERROR: a Laplace.Cli ingest is already running — wait for it to finish or stop it before seed-step %STEP%
  exit /b 2
)
call :ensure_cli || exit /b 1
echo ==== seed-step: ingest %STEP% %EXTRA% ====
"%LAPLACE_CLI_EXE%" ingest %STEP% %EXTRA%
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo ERROR: seed-step %STEP%: Laplace.Cli exited %RC%
  if %RC% LSS 0 echo   ^(negative exit = NTSTATUS crash code^)
  exit /b %RC%
)
call :wait_cli_exit
call :verify_step
exit /b %ERRORLEVEL%

:ensure_cli
rem Ingest CLI is a ReadyToRun publish tree (net10.0-r2r), not the plain build
rem output. Publishing into BaseOutputPath silently kept non-R2R assemblies and
rem left clrjit on the hot path (WER 0xc0000409 on ConceptNet/WordNet 2026-07-11).
set "CLI_R2R_OUT=%LAPLACE_BUILD_ROOT%\app\bin\Laplace.Cli\Release\net10.0-r2r"
set "LAPLACE_CLI_EXE=%CLI_R2R_OUT%\Laplace.Cli.exe"
if "%REBUILD%"=="1" goto ensure_cli_build
if defined LAPLACE_FORCE_CLI_BUILD goto ensure_cli_build
if not exist "%LAPLACE_CLI_EXE%" goto ensure_cli_build
if not exist "%LAPLACE_CLI_EXE%.r2r-stamp" goto ensure_cli_build

rem FRESHNESS, not existence. The r2r tree is separate from the plain build output,
rem so rebuild-all can rebuild every assembly while this exe stays old — a seed then
rem silently runs pre-fix ingest code. Republish whenever any built assembly is newer
rem than the r2r exe. No env var, no flag: correct by default.
set "CLI_PLAIN_OUT=%LAPLACE_BUILD_ROOT%\app\bin\Laplace.Cli\Release\net10.0"
powershell -NoProfile -Command ^
  "$r = '%LAPLACE_CLI_EXE%'; $p = '%CLI_PLAIN_OUT%';" ^
  "if (-not (Test-Path $r)) { exit 1 };" ^
  "$rt = (Get-Item $r).LastWriteTimeUtc;" ^
  "$n = Get-ChildItem -Path $p -Filter *.dll -ErrorAction SilentlyContinue |" ^
  "     Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1;" ^
  "if ($n -and $n.LastWriteTimeUtc -gt $rt) { exit 1 };" ^
  "exit 0"
if errorlevel 1 (
  echo seed-step: CLI ReadyToRun tree is older than the built assemblies — republishing
  goto ensure_cli_build
)
exit /b 0
:ensure_cli_build
echo ==== seed-step: publish CLI Release ^(ReadyToRun → net10.0-r2r^) ====
rem Fix for clrjit.dll 0xc0000409 fail-fast (.NET 10.0.9 WER on ConceptNet/WordNet):
rem publish the managed closure as ReadyToRun into a dedicated output dir so ingest
rem does not depend on runtime tiered recompile. DOTNET_TieredCompilation=0 is NOT
rem the fix — do not re-add it.
if exist "%CLI_R2R_OUT%" rmdir /s /q "%CLI_R2R_OUT%"
dotnet publish "%LAPLACE_ROOT%\app\Laplace.Cli\Laplace.Cli.csproj" -c Release -v q --nologo ^
  -p:PublishReadyToRun=true -o "%CLI_R2R_OUT%" || exit /b 1
if not exist "%LAPLACE_CLI_EXE%" (
  echo ERROR: CLI ReadyToRun publish succeeded but exe missing: %LAPLACE_CLI_EXE%
  exit /b 1
)
echo r2r %DATE% %TIME%> "%LAPLACE_CLI_EXE%.r2r-stamp"
exit /b 0

rem exit /b 0 = an ingest CLI process is running, 1 = none (same convention as
rem the old tasklist^|find check, so callers keep `if not errorlevel 1` = running).
:cli_running
powershell -NoProfile -Command "if (Get-CimInstance Win32_Process | Where-Object { ($_.Name -eq 'dotnet.exe' -or $_.Name -eq 'Laplace.Cli.exe') -and $_.CommandLine -match 'Laplace\.Cli' }) { exit 0 } else { exit 1 }"
exit /b %ERRORLEVEL%

:wait_cli_exit
call :cli_running
if errorlevel 1 exit /b 0
timeout /t 2 /nobreak >nul 2>nul
goto wait_cli_exit

rem Independent post-step verification (.scratchpad/02 Issue 13): don't trust the
rem CLI's self-printed summary — ask the database whether evidence from this step's
rem source actually landed.
:verify_step
set "STEP_SOURCE="
if /i "%STEP%"=="safetensors"   goto verify_model_step
if /i "%STEP%"=="unicode"       set "STEP_SOURCE=UnicodeDecomposer"
if /i "%STEP%"=="iso639"        set "STEP_SOURCE=ISO639Decomposer"
if /i "%STEP%"=="cili"          set "STEP_SOURCE=CILIDecomposer"
if /i "%STEP%"=="wordnet"       set "STEP_SOURCE=WordNetDecomposer"
if /i "%STEP%"=="omw"           set "STEP_SOURCE=OMWDecomposer"
if /i "%STEP%"=="verbnet"       set "STEP_SOURCE=VerbNetDecomposer"
if /i "%STEP%"=="propbank"      set "STEP_SOURCE=PropBankDecomposer"
if /i "%STEP%"=="framenet"      set "STEP_SOURCE=FrameNetDecomposer"
if /i "%STEP%"=="semlink"       set "STEP_SOURCE=SemLinkDecomposer"
if /i "%STEP%"=="mapnet"        set "STEP_SOURCE=MapNetDecomposer"
if /i "%STEP%"=="wordframenet"  set "STEP_SOURCE=WordFrameNetDecomposer"
if /i "%STEP%"=="conceptnet"    set "STEP_SOURCE=ConceptNetDecomposer"
if /i "%STEP%"=="atomic2020"    set "STEP_SOURCE=Atomic2020Decomposer"
if /i "%STEP%"=="ud"            set "STEP_SOURCE=UDDecomposer"
if /i "%STEP%"=="wiktionary"    set "STEP_SOURCE=WiktionaryDecomposer"
if /i "%STEP%"=="tatoeba"       set "STEP_SOURCE=TatoebaDecomposer"
if /i "%STEP%"=="opensubtitles" set "STEP_SOURCE=OpenSubtitlesDecomposer"
if /i "%STEP%"=="document"      set "STEP_SOURCE=UserPrompt"
if /i "%STEP%"=="stack"         set "STEP_SOURCE=StackDecomposer"
if /i "%STEP%"=="repo"          set "STEP_SOURCE=RepoDecomposer"
if /i "%STEP%"=="tiny-codes"    set "STEP_SOURCE=TinyCodesDecomposer"
if /i "%STEP%"=="chess"         set "STEP_SOURCE=ChessPgn"
if /i "%STEP%"=="chess-books"   set "STEP_SOURCE=ChessBook"
if /i "%STEP%"=="openings"      set "STEP_SOURCE=ChessOpenings"
if not defined STEP_SOURCE (
  echo ERROR: seed-step verify: no source mapping for '%STEP%'
  if defined LAPLACE_SKIP_VERIFY exit /b 0
  exit /b 3
)
set "STEP_EVIDENCE="
for /f "usebackq delims=" %%v in (`psql -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -tAc "SELECT laplace.evidence_count(NULL, laplace.source_id('%STEP_SOURCE%'));"`) do set "STEP_EVIDENCE=%%v"
if not defined STEP_EVIDENCE goto verify_fail
if "%STEP_EVIDENCE%"=="0" goto verify_fail
echo ==== seed-step verify: %STEP_SOURCE% evidence_count=%STEP_EVIDENCE% ====
exit /b 0
:verify_fail
echo ERROR: post-step verification failed — evidence_count for %STEP_SOURCE% returned '%STEP_EVIDENCE%' (db=%LAPLACE_DBNAME% @ %LAPLACE_PGHOST%)
exit /b 3

rem A model's source id is a content hash over its config+weights (ModelDecomposer
rem .SourceForModel), so it cannot be recomputed here. The deposit registers the
rem source's name via HAS_NAME_ALIAS (BootstrapIntentBuilder), so verification
rem resolves name -> source id(s) through consensus and sums their evidence.
:verify_model_step
set "STEP_SOURCE="
for /f "usebackq delims=" %%m in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0derive-model-source.ps1" "%EXTRA%"`) do set "STEP_SOURCE=%%m"
if not defined STEP_SOURCE (
  echo ERROR: seed-step verify: could not derive model source name from '%EXTRA%'
  if defined LAPLACE_SKIP_VERIFY exit /b 0
  exit /b 3
)
set "STEP_EVIDENCE="
for /f "usebackq delims=" %%v in (`psql -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -tAc "SELECT COALESCE(SUM(laplace.evidence_count(NULL, c.subject_id)), 0) FROM laplace.consensus c WHERE c.type_id = laplace.relation_type_id('HAS_NAME_ALIAS') AND c.object_id = laplace.word_id('%STEP_SOURCE%');"`) do set "STEP_EVIDENCE=%%v"
if not defined STEP_EVIDENCE goto verify_fail
if "%STEP_EVIDENCE%"=="0" goto verify_fail
echo ==== seed-step verify: %STEP_SOURCE% evidence_count=%STEP_EVIDENCE% ====
exit /b 0

:run_model_tinyllama
call :resolve_model LAPLACE_MODEL_TINYLLAMA LAPLACE_TINYLLAMA_DIR "models--TinyLlama--TinyLlama-1.1B-Chat-v1.0" TINYLLAMA || exit /b 1
set "EXTRA=!TINYLLAMA!"
set "STEP=safetensors"
goto run_ingest_path

:run_model_phi
call :resolve_model LAPLACE_MODEL_PHI LAPLACE_PHI2_DIR "models--microsoft--phi-2" PHI || exit /b 1
set "EXTRA=!PHI!"
set "STEP=safetensors"
goto run_ingest_path

:run_model_qwen
call :resolve_model LAPLACE_MODEL_QWEN25_CODER LAPLACE_QWEN25_CODER_DIR "models--Qwen--Qwen2.5-Coder-3B-Instruct" QWEN || exit /b 1
set "EXTRA=!QWEN!"
set "STEP=safetensors"
goto run_ingest_path

:resolve_model
set "%~4="
set "_resolved="
call set "_resolved=%%%~1%%"
if not defined _resolved call set "_resolved=%%%~2%%"
if defined _resolved (
  if exist "!_resolved!\config.json" if exist "!_resolved!\tokenizer.json" (
    set "%~4=!_resolved!"
    echo resolved %~4: !_resolved!
    exit /b 0
  )
  echo ERROR: %~1 is set but not a complete HF snapshot: !_resolved!
  exit /b 1
)
set "_fam=%LAPLACE_MODEL_HUB%\%~3"
if not exist "!_fam!" (
  echo ERROR: model not found — set %~1 or download into !_fam!\snapshots\<rev>\
  exit /b 1
)
for /d %%s in ("!_fam!\snapshots\*") do (
  if exist "%%s\config.json" if exist "%%s\tokenizer.json" (
    dir /b "%%s\*.safetensors" >nul 2>&1
    if not errorlevel 1 (
      set "%~4=%%s"
      echo resolved %~4: %%s
      exit /b 0
    )
  )
)
echo ERROR: no weighted snapshot under !_fam!\snapshots\
exit /b 1

:list
echo seed-step commands (run one at a time):
echo   floor:     unicode  iso639
echo   document:  document ^<path^>
echo   knowledge: wordnet  omw  verbnet  propbank  framenet  mapnet  wordframenet  semlink  conceptnet  atomic2020  ud  wiktionary
echo   usage:     tatoeba  opensubtitles
echo   chess:     chess ^<path^>  openings ^<path^>  chess-books ^<path^>
echo   code:      stack ^<path^>  repo ^<path^>  tiny-codes ^<path^>
echo   models:    model-tinyllama  model-phi  model-qwen  safetensors ^<snapshot-dir^>
echo.
echo stages: seed-stage floor ^| document ^| knowledge ^| usage ^| code ^| models
exit /b 0

:usage
echo usage: seed-step.cmd [--rebuild] ^<step^> [path]
echo        seed-step.cmd --list
echo.
echo examples:
echo   seed-step.cmd unicode
echo   seed-step.cmd --rebuild wordnet
echo   seed-step.cmd document "%INGEST%\test-data\text"
echo   seed-step.cmd repo "%REPOS%\Laplace"
echo.
echo CLI is published ReadyToRun only when missing or --rebuild is passed.
exit /b 2

:unknown_step
if defined LAPLACE_SKIP_VERIFY exit /b 0
exit /b 3
