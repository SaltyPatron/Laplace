@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd" || exit /b 1
cd /d "%LAPLACE_ROOT%"

set "ERR=0"
rem Each step's ingest refreshes the statistics of what it wrote, as the seed role (which may
rem MAINTAIN the substrate tables, by the declared access db-reset applies), and fails if any
rem table was skipped (Laplace#1879). No step runs as the tables' owner.

echo ===== seed-foundation started %DATE% %TIME% =====

for %%S in (unicode iso639 operational cili wordnet verbnet propbank framenet mapnet wordframenet semlink) do (
  echo.
  echo ==== %%S %TIME% ====
  call "%~dp0seed-step.cmd" %%S
  rem NTSTATUS crash codes are negative, so `if errorlevel 1` misses them
  if not "!ERRORLEVEL!"=="0" (
    echo FAILED: %%S exit=!ERRORLEVEL!
    set "ERR=1"
    goto done
  )
  echo OK: %%S
)

:done
echo.
echo ===== seed-foundation finished %DATE% %TIME% err=!ERR! =====
exit /b !ERR!
