@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

set "ERR=0"
set "LAPLACE_DEFER_PHYS_INDEX_REBUILD=1"

echo ===== seed-foundation started %DATE% %TIME% =====

for %%S in (unicode iso639 cili wordnet omw) do (
  echo.
  echo ==== %%S %TIME% ====
  call "%~dp0seed-step.cmd" %%S
  if errorlevel 1 (
    echo FAILED: %%S exit=!ERRORLEVEL!
    set "ERR=1"
    goto done
  )
  echo OK: %%S
)

:done
set "LAPLACE_DEFER_PHYS_INDEX_REBUILD="
if "!ERR!"=="0" (
  call "%~dp0rebuild-phys-indexes.cmd"
  if errorlevel 1 set "ERR=1"
)
echo.
echo ===== seed-foundation finished %DATE% %TIME% err=!ERR! =====
exit /b !ERR!
