@echo off
setlocal
rem ==== Invention-complete witness seed (manifest-driven) ===================
rem Drop/recreate laplace, run lexical ladder in dependency order, then functionality.
rem
rem *Net link law (synset hub):
rem   WordNet  — synsets + senses + lemma arena + pointer graph (anchor)
rem   OMW      — same WordNetSynset IDs: translations/defs/examples per language
rem   VerbNet  — lemma CORRESPONDS_TO wordnet/sense/{key}
rem   PropBank — lemma HAS_SENSE roleset; roleset CORRESPONDS_TO verbnet/class
rem   FrameNet — lemma EVOKES_FRAME framenet/frame
rem   SemLink  — explicit propbank↔verbnet and verbnet↔framenet alignment
rem   ConceptNet / Atomic2020 — en-scoped graphs sharing the lemma arena (not synset IDs)
rem
rem English scope: LAPLACE_INGEST_LANGS=en (+ UD en_* treebanks, Wiktionary English jsonl).
rem Skip: LAPLACE_SKIP_MODELS (safetensors), LAPLACE_SKIP_USAGE (tatoeba/opensubtitles).
rem cwd on D: so /vault junctions resolve to D:\Data\Ingest.
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
set "PGPASSWORD=postgres"
set "PGBIN=C:\Program Files\PostgreSQL\18\bin"
set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=laplace"
set "LAPLACE_PERFCACHE_BIN=%LAPLACE_ROOT%\build-win\core\perfcache\laplace_t0_perfcache.bin"
set "INGEST=D:\Data\Ingest"
set "REPOS=D:\Repositories"
set "MODELS=D:\Models\hub"
set "LAPLACE_INGEST_LANGS=en"
if not defined LAPLACE_EMIT_CROSS_LANG set "LAPLACE_EMIT_CROSS_LANG=0"
set "LAPLACE_INGEST_WORKERS=4"
set "LAPLACE_DECOMPOSE_WORKERS=1"
set "LAPLACE_COPY_VALIDATE=1"
if not defined LAPLACE_FOLD_WORKERS set "LAPLACE_FOLD_WORKERS=8"
if not defined LAPLACE_SKIP_MODELS set "LAPLACE_SKIP_MODELS=1"
if not defined LAPLACE_SKIP_USAGE set "LAPLACE_SKIP_USAGE=1"

echo ==== DROP + recreate laplace ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='laplace' AND pid<>pg_backend_pid();" -c "DROP DATABASE IF EXISTS laplace;" || exit /b 1
"%PGBIN%\createdb.exe" -h localhost -U postgres laplace || exit /b 1
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS postgis;" -c "CREATE EXTENSION IF NOT EXISTS laplace_geom;" -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate;" || exit /b 1

echo ==== post-create identity health ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -v ON_ERROR_STOP=1 -c "SET search_path = laplace, public; SELECT * FROM substrate_health();" || exit /b 1

cd app
dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1

rem ---- L0–L1: floor + language registry ------------------------------------
echo ==== ingest unicode (L0) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest unicode || exit /b 1
echo ==== ingest iso639 (L1) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest iso639 || exit /b 1

rem ---- *Net cluster: WordNet synset hub, then extensions in link order ------
echo ==== ingest wordnet (synsets + senses + lemma arena) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest wordnet || exit /b 1
echo ==== ingest omw (bind lemmas/defs to WordNetSynset IDs, en) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest omw || exit /b 1
echo ==== ingest verbnet (CORRESPONDS_TO wordnet/sense) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest verbnet || exit /b 1
echo ==== ingest propbank (HAS_SENSE roleset; CORRESPONDS_TO verbnet) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest propbank || exit /b 1
echo ==== ingest framenet (EVOKES_FRAME from lemmas) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest framenet || exit /b 1
echo ==== ingest semlink (propbank-verbnet + verbnet-framenet alignment) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest semlink || exit /b 1
echo ==== ingest conceptnet (/c/en/ graph, shared lemma arena) ====
set "LAPLACE_INGEST_WORKERS=1"
set "LAPLACE_INGEST_COMMIT_ROWS=50000"
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest conceptnet || exit /b 1
set "LAPLACE_INGEST_WORKERS=4"
set "LAPLACE_INGEST_COMMIT_ROWS="
echo ==== ingest atomic2020 (script relations on event atoms) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest atomic2020 || exit /b 1

rem ---- Syntax + dictionary (English-scoped; not synset-linked) --------------
echo ==== ingest ud (en_* treebanks) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest ud || exit /b 1
echo ==== ingest wiktionary (English entries only) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest wiktionary || exit /b 1

rem ---- Functionality: repos + code corpora --------------------------------
for %%r in (Laplace X_BONEYARD llama-workspace SpecEditor TournamentManager Laplace-Space-Game temp-llama-models) do (
  if exist "%REPOS%\%%r" (
    echo ==== ingest repo %%r ====
    dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest repo "%REPOS%\%%r" || exit /b 1
  ) else (
    echo ==== [skip] repo not found: %REPOS%\%%r ====
  )
)

if exist "%INGEST%\tiny-codes" (
  echo ==== ingest tiny-codes ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest tiny-codes "%INGEST%\tiny-codes" || exit /b 1
) else (
  echo ==== [skip] tiny-codes — run scripts\win\download-code-data.cmd tiny-codes ====
)
if exist "%INGEST%\stack-v2" (
  echo ==== ingest stack-v2 ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest stack "%INGEST%\stack-v2" || exit /b 1
) else (
  echo ==== [skip] stack-v2 — run scripts\win\download-code-data.cmd stack-v2 ====
)

if not defined LAPLACE_SKIP_USAGE (
  for %%s in (tatoeba opensubtitles) do (
    echo ==== ingest %%s ====
    dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest %%s || exit /b 1
  )
) else (
  echo ==== [skip] tatoeba + opensubtitles — LAPLACE_SKIP_USAGE=1 ====
)

echo ==== ingest image (test-data annex) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest image "%INGEST%\test-data\images" || exit /b 1
echo ==== ingest audio (test-data annex) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest audio "%INGEST%\test-data\audio" || exit /b 1

if not defined LAPLACE_SKIP_MODELS (
  for %%m in (
    "%MODELS%\models--TinyLlama--TinyLlama-1.1B-Chat-v1.0"
    "%MODELS%\models--microsoft--phi-2"
    "%MODELS%\models--Qwen--Qwen2.5-Coder-3B-Instruct"
  ) do (
    if exist %%m (
      for /d %%s in ("%%m\snapshots\*") do (
        if exist "%%s\tokenizer.json" if exist "%%s\config.json" (
          echo ==== deposit safetensors %%s ====
          dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest safetensors "%%s" || exit /b 1
        )
      )
    ) else (
      echo ==== [skip] safetensor snapshot family not found: %%m ====
    )
  )
) else (
  echo ==== [skip] safetensor snapshots — LAPLACE_SKIP_MODELS=1 ====
)

cd /d "%LAPLACE_ROOT%"
echo ==== substrate audit ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -f "%LAPLACE_ROOT%\scripts\sql\substrate-audit.sql" || exit /b 1

echo ==== SEED-SUBSTRATE COMPLETE ====
