@echo off
setlocal
call "%~dp0env.cmd"

set "VERDICT_DB=%~1"
if not defined VERDICT_DB set "VERDICT_DB=laplace_export"
set "OUTDIR=%LAPLACE_ROOT%\build-win\verdicts"
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo ==== B2 index rebuild on %VERDICT_DB% (minutes at 1e8+ rows) ====
psql -h localhost -U postgres -d %VERDICT_DB% -P pager=off ^
    -f "%LAPLACE_ROOT%\scripts\sql\rebuild-consensus-indexes.sql" || exit /b 1

echo ==== model-planes-audit on %VERDICT_DB% ====
psql -h localhost -U postgres -d %VERDICT_DB% -P pager=off ^
    -f "%LAPLACE_ROOT%\scripts\sql\model-planes-audit.sql" ^
    -o "%OUTDIR%\model-planes-%VERDICT_DB%.txt" || exit /b 1

echo ==== substrate law probes on %VERDICT_DB% ====
psql -h localhost -U postgres -d %VERDICT_DB% -P pager=off ^
    -f "%LAPLACE_ROOT%\scripts\sql\substrate-law-probes.sql" ^
    -o "%OUTDIR%\law-probes-%VERDICT_DB%.txt" || exit /b 1

type "%OUTDIR%\model-planes-%VERDICT_DB%.txt"
type "%OUTDIR%\law-probes-%VERDICT_DB%.txt"
echo.
echo verdicts written to %OUTDIR%\model-planes-%VERDICT_DB%.txt and law-probes-%VERDICT_DB%.txt
