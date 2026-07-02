@echo off
setlocal EnableDelayedExpansion
rem verify-model.cmd <model.gguf> [min-pass] -- the truth harness for foundry GGUFs.
rem
rem Gates on CONTENT, not exit codes (the FAITHFUL/LOOKUP lesson: models that
rem "load and emit tokens" can still be knowledge-free). Three legs:
rem   1. behavioral: substrate-derived expected continuations per probe word,
rem      with explicit global-hub-collapse and empty-output detectors
rem      (scripts/verify-model-behavioral.py).
rem   2. perplexity (informational until the tokenizer can encode arbitrary
rem      text): llama-perplexity over substrate-rendered held-out sentences.
rem   3. oracle (optional, LAPLACE_ORACLE_TOKENIZER_DIR set): exact f64
rem      forward pass cross-check via scripts/model-forward-oracle.py.
rem
rem Exit 0 only if the behavioral gate passes.

call "%~dp0env.cmd"

if "%~1"=="" (
  echo usage: verify-model.cmd ^<model.gguf^> [min-pass]
  exit /b 2
)
set "MODEL=%~1"
set "MINPASS=%~2"
if "%MINPASS%"=="" set "MINPASS=0.5"
if not exist "%MODEL%" (
  echo verify-model: model not found: %MODEL%
  exit /b 2
)
set "LLAMA_DIR=%LAPLACE_LLAMA_DIR%"
if "%LLAMA_DIR%"=="" set "LLAMA_DIR=D:\LlamaCPP"

echo === [1/3] behavioral content gate =========================================
python "%~dp0..\verify-model-behavioral.py" --model "%MODEL%" ^
  --llama "%LLAMA_DIR%\llama-completion.exe" --min-pass %MINPASS% ^
  --report "%MODEL%.behavioral.json"
set "BEHAVIORAL_RC=%ERRORLEVEL%"

echo === [2/3] perplexity (informational) ======================================
set "PPLFILE=%TEMP%\laplace_ppl_sentences.txt"
psql -h localhost -U postgres -d laplace -X -q -t -A -c "SET search_path=laplace,public; SELECT render_text(e.id) FROM entities e WHERE e.tier=3 ORDER BY e.id LIMIT 200;" > "%PPLFILE%" 2>nul
for %%S in ("%PPLFILE%") do set PPLSIZE=%%~zS
if "!PPLSIZE!"=="" set PPLSIZE=0
if !PPLSIZE! GTR 500 (
  "%LLAMA_DIR%\llama-perplexity.exe" -m "%MODEL%" -f "%PPLFILE%" 2>&1 | findstr /I "estimate PPL perplexity"
) else (
  echo verify-model: no substrate sentences available for perplexity, skipped
)

echo === [3/3] forward oracle (optional) =======================================
if not "%LAPLACE_ORACLE_TOKENIZER_DIR%"=="" (
  python "%~dp0..\model-forward-oracle.py" forward-gguf "%MODEL%" "the" "%LAPLACE_ORACLE_TOKENIZER_DIR%"
) else (
  echo verify-model: LAPLACE_ORACLE_TOKENIZER_DIR not set, oracle leg skipped
)

if not "%BEHAVIORAL_RC%"=="0" (
  echo verify-model: FAIL ^(behavioral rc=%BEHAVIORAL_RC%^)
  exit /b 1
)
echo verify-model: PASS
exit /b 0
