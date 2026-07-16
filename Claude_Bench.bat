@echo off
rem Claude_Bench — local-vs-wire benchmark matrix (hart-desktop PG vs hart-server PG).
rem Owner: Claude. Operates ONLY on the laplace_bench database on each host — never
rem touches 'laplace'. Each run nukes + remigrates laplace_bench on both hosts so
rem ingest timings start from an identical clean slate.
rem Usage:  Claude_Bench.bat                 (full matrix: ingest + queries)
rem         Claude_Bench.bat -SkipIngest     (queries only, keeps existing bench data)
rem         Claude_Bench.bat -QueryReps 11   (more read-side repetitions)
rem Report: D:\Data\Output\bench\<timestamp>\  (bench.csv + summary.md)
setlocal
cd /d "%~dp0"
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\win\bench-matrix.ps1" %*
set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="0" (echo ===== Claude_Bench OK =====) else (echo ===== Claude_Bench FAILED exit=%RC% =====)
pause
exit /b %RC%
