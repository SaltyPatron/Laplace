@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd" || exit /b 1
cd /d "%LAPLACE_ROOT%"

echo ===== finish-lexical-king %DATE% %TIME% =====

if not exist "%LAPLACE_ROOT%\build-win\core\laplace_core.dll" (
  call "%~dp0build-engine-libs.cmd" || exit /b 1
) else (
  echo engine libs present — skipping rebuild ^(perfcache may be locked by PG^)
)

dotnet build app\Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1

call "%~dp0install-extensions.cmd" || exit /b 1
psql -U postgres -d laplace -v ON_ERROR_STOP=1 -f "%~dp0_apply-lexical-peers-symmetric.sql"
call "%~dp0verify-lexical-peers-installed.cmd" || exit /b 1

for %%S in (unicode iso639 cili wordnet verbnet propbank framenet mapnet wordframenet semlink wiktionary) do (
  echo.
  echo ==== seed %%S %TIME% ====
  call "%~dp0seed-step.cmd" %%S || exit /b 1
  echo OK: %%S
)

call "%~dp0verify-king-define.cmd" || exit /b 1

echo.
echo ===== finish-lexical-king COMPLETE %DATE% %TIME% =====
exit /b 0
