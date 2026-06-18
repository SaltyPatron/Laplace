@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"

set "PSQL=%PGBIN%\psql.exe"
set "KNOWLEDGE=wordnet omw verbnet propbank framenet semlink conceptnet atomic2020 ud wiktionary"

echo ==== seed-continue: inspect layer completion ====
for %%s in (unicode iso639 wordnet omw verbnet propbank framenet semlink conceptnet atomic2020 ud wiktionary tatoeba opensubtitles) do (
  set "SRC=%%s"
  if /i "%%s"=="unicode" set "SRC=UnicodeDecomposer"
  if /i "%%s"=="iso639" set "SRC=ISO639Decomposer"
  if /i "%%s"=="wordnet" set "SRC=WordNetDecomposer"
  if /i "%%s"=="omw" set "SRC=OMWDecomposer"
  if /i "%%s"=="verbnet" set "SRC=VerbNetDecomposer"
  if /i "%%s"=="propbank" set "SRC=PropBankDecomposer"
  if /i "%%s"=="framenet" set "SRC=FrameNetDecomposer"
  if /i "%%s"=="semlink" set "SRC=SemLinkDecomposer"
  if /i "%%s"=="conceptnet" set "SRC=ConceptNetDecomposer"
  if /i "%%s"=="atomic2020" set "SRC=Atomic2020Decomposer"
  if /i "%%s"=="ud" set "SRC=UDDecomposer"
  if /i "%%s"=="wiktionary" set "SRC=WiktionaryDecomposer"
  if /i "%%s"=="tatoeba" set "SRC=TatoebaDecomposer"
  if /i "%%s"=="opensubtitles" set "SRC=OpenSubtitlesDecomposer"
  for /f "usebackq delims=" %%c in (`"%PSQL%" -h localhost -U postgres -d laplace -tAc "SELECT CASE WHEN laplace.layer_complete(laplace.source_id('!SRC!')) THEN 'done' ELSE 'pending' END"`) do set "STAT_%%s=%%c"
)

if not "!STAT_unicode!"=="done" call "%~dp0seed-stage.cmd" floor || exit /b 1
if exist "!INGEST!\test-data\text" (
  echo ==== document stage ====
  call "%~dp0seed-step.cmd" document "!INGEST!\test-data\text" || exit /b 1
) else (
  call "%~dp0seed-stage.cmd" document || exit /b 1
)

for %%s in (%KNOWLEDGE%) do (
  if not "!STAT_%%s!"=="done" (
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
