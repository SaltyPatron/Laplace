@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd" || exit /b 1
cd /d "%LAPLACE_ROOT%"

set "ERR=0"
echo ===== seed-post-wiktionary started %DATE% %TIME% =====

echo [%TIME%] waiting for wiktionary ingest to finish...
:wait_wikt
tasklist /FI "IMAGENAME eq Laplace.Cli.exe" 2>nul | "%SystemRoot%\System32\find.exe" /I "Laplace.Cli.exe" >nul
if not errorlevel 1 (
  timeout /t 30 /nobreak >nul 2>nul
  goto wait_wikt
)

findstr /C:"OK: wiktionary" "%LAPLACE_ROOT%\seed-wiktionary.log" >nul 2>&1
if errorlevel 1 (
  findstr /C:"INGEST_COMPLETE" "%LAPLACE_ROOT%\seed-wiktionary.log" >nul 2>&1
  if errorlevel 1 (
    echo FAILED: wiktionary did not complete — see seed-wiktionary.log
    exit /b 1
  )
)
echo [%TIME%] wiktionary done

for %%S in (omw ud) do (
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

echo.
echo ==== document %TIME% ====
call "%~dp0seed-step.cmd" document "%INGEST%\test-data\text"
if errorlevel 1 (
  echo FAILED: document exit=!ERRORLEVEL!
  set "ERR=1"
  goto done
)
echo OK: document

echo.
echo ==== chess %TIME% ====
call "%~dp0seed-step.cmd" chess "%INGEST%\Games\Chess"
if errorlevel 1 (
  echo FAILED: chess exit=!ERRORLEVEL!
  set "ERR=1"
  goto done
)
echo OK: chess

:done
echo.
echo ===== seed-post-wiktionary finished %DATE% %TIME% err=!ERR! =====
exit /b !ERR!

