@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"

set "KNOWLEDGE=wordnet omw verbnet propbank framenet mapnet wordframenet semlink conceptnet atomic2020 ud wiktionary"

echo ==== seed-continue: inspect layer completion (batched) ====
for /f "usebackq delims=" %%c in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0seed-layer-check-batch.ps1"`) do set "%%c"

if not "!STAT_unicode!"=="t" call "%~dp0seed-stage.cmd" floor || exit /b 1

if exist "!INGEST!\test-data\text" (
  if not "!STAT_document!"=="t" (
    echo ==== document stage ====
    call "%~dp0seed-step.cmd" document "!INGEST!\test-data\text" || exit /b 1
  ) else (
    echo ==== [ok] document layer_complete ====
  )
) else (
  call "%~dp0seed-stage.cmd" document || exit /b 1
)

for %%s in (%KNOWLEDGE%) do (
  if not "!STAT_%%s!"=="t" (
    echo ==== resume knowledge: %%s ====
    call "%~dp0seed-step.cmd" %%s || exit /b 1
  ) else (
    echo ==== [ok] %%s layer_complete ====
  )
)

call "%~dp0seed-stage.cmd" usage || exit /b 1
call "%~dp0seed-stage.cmd" code || exit /b 1
if /i not "%LAPLACE_SKIP_MODELS%"=="1" (
  call "%~dp0seed-stage.cmd" models || exit /b 1
) else (
  echo ==== models stage skipped ^(LAPLACE_SKIP_MODELS=1^) ====
)

cd /d "%LAPLACE_ROOT%"
echo ==== SEED-CONTINUE COMPLETE ====
exit /b 0
