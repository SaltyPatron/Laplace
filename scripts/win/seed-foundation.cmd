@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd" || exit /b 1
cd /d "%LAPLACE_ROOT%"

set "ERR=0"
rem each step's own ANALYZE is deferred: the whole seed is analyzed once, below
set "LAPLACE_SEED_NO_ANALYZE=1"

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
if "!ERR!"=="0" (
  echo.
  call "%~dp0analyze-substrate.cmd"
  if not "!ERRORLEVEL!"=="0" (
    echo FAILED: analyze-substrate exit=!ERRORLEVEL!
    set "ERR=1"
  )
)
echo.
echo ===== seed-foundation finished %DATE% %TIME% err=!ERR! =====
exit /b !ERR!
