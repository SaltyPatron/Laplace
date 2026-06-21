@echo off

setlocal EnableDelayedExpansion

call "%~dp0env.cmd"

cd /d "%LAPLACE_ROOT%\app"



set "KNOWLEDGE=wordnet omw verbnet propbank framenet mapnet wordframenet semlink conceptnet atomic2020 ud wiktionary"



echo ==== seed-continue: inspect layer completion ====

for %%s in (unicode iso639 document wordnet omw verbnet propbank framenet semlink conceptnet atomic2020 ud wiktionary tatoeba opensubtitles) do (

  call :map_source %%s

  for /f "usebackq delims=" %%c in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0seed-layer-check.ps1" -Key %%s -Src !SRC! -Layer !LAYER!`) do set "%%c"

)



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



:map_source

set "SRC=%~1"

set "LAYER=2"

if /i "%SRC%"=="unicode" set "SRC=UnicodeDecomposer" & set "LAYER=0"

if /i "%SRC%"=="iso639" set "SRC=ISO639Decomposer" & set "LAYER=1"

if /i "%SRC%"=="wordnet" set "SRC=WordNetDecomposer"

if /i "%SRC%"=="omw" set "SRC=OMWDecomposer" & set "LAYER=3"

if /i "%SRC%"=="verbnet" set "SRC=VerbNetDecomposer"

if /i "%SRC%"=="propbank" set "SRC=PropBankDecomposer"

if /i "%SRC%"=="framenet" set "SRC=FrameNetDecomposer" & set "LAYER=3"

if /i "%SRC%"=="semlink" set "SRC=SemLinkDecomposer" & set "LAYER=3"

if /i "%SRC%"=="conceptnet" set "SRC=ConceptNetDecomposer"

if /i "%SRC%"=="atomic2020" set "SRC=Atomic2020Decomposer"

if /i "%SRC%"=="ud" set "SRC=UDDecomposer"

if /i "%SRC%"=="wiktionary" set "SRC=WiktionaryDecomposer"

if /i "%SRC%"=="tatoeba" set "SRC=TatoebaDecomposer"

if /i "%SRC%"=="opensubtitles" set "SRC=OpenSubtitlesDecomposer"

if /i "%SRC%"=="document" set "SRC=UserPrompt"

exit /b 0


