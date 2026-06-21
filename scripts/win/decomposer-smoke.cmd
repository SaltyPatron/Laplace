@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

if not defined LAPLACE_CANONICAL_DB set "LAPLACE_CANONICAL_DB=laplace"

echo ==== smoke: test unicode, promote to fresh !LAPLACE_CANONICAL_DB! ====

echo ==== fresh canonical DB: !LAPLACE_CANONICAL_DB! ====
call "%~dp0db-isolate.cmd" "!LAPLACE_CANONICAL_DB!" || exit /b 1

call "%~dp0decomposer-test.cmd" unicode || exit /b 1
call "%~dp0decomposer-promote.cmd" unicode || exit /b 1

for /f "usebackq delims=" %%u in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0seed-layer-check.ps1" -Key unicode -Src UnicodeDecomposer -Layer 0 -Dbname "!LAPLACE_CANONICAL_DB!"`) do set "%%u"
if not "!STAT_unicode!"=="t" (
  echo ERROR: !LAPLACE_CANONICAL_DB! missing unicode layer after promote
  exit /b 1
)

echo ==== SMOKE OK: unicode promoted to !LAPLACE_CANONICAL_DB! ====
exit /b 0
