@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"
set "Platform="
set "PlatformTarget="

set "FILTER="
set "ARGS="
:parse
if "%~1"=="" goto run
set "A=%~1"
set "TAKEN="
if not defined FILTER if not "%A:~0,1%"=="-" ( set "FILTER=%A%" & set "TAKEN=1" )
if not defined TAKEN set "ARGS=!ARGS! %1"
shift /1
goto parse

:run
set "ANYFAIL="
set "MATCHED="

set "XUNIT_TIER_EXCLUDE=Tier^!=perf"
for %%P in (
  Laplace.Engine.Core.Tests
  Laplace.Engine.Dynamics.Tests
  Laplace.Engine.Synthesis.Tests
  Laplace.Decomposers.Abstractions.Tests
  Laplace.Decomposers.Containers.Abstractions.Tests
  Laplace.SubstrateCRUD.Tests
  Laplace.Ingestion.Tests
  Laplace.Decomposers.Tests
  Laplace.Endpoints.OpenAICompat.Tests
  Laplace.Modality.Chess.Tests
  Laplace.Chess.Service.Tests
  Laplace.Chess.Uci.Tests
) do (
  set "RUNIT=1"
  if defined FILTER (
    echo %%P | findstr /i /c:"%FILTER%" >nul || set "RUNIT="
  )
  if defined RUNIT (
    set "MATCHED=1"
    if "!CONTINUE_ON_FAIL!"=="1" (
      dotnet test "%%P\%%P.csproj" -c Release -v minimal --nologo --filter "!XUNIT_TIER_EXCLUDE!" !ARGS! || set "ANYFAIL=1"
    ) else (
      dotnet test "%%P\%%P.csproj" -c Release -v minimal --nologo --filter "!XUNIT_TIER_EXCLUDE!" !ARGS! || exit /b 1
    )
  )
)
if defined FILTER if not defined MATCHED (
  echo test-app: no test project matches "%FILTER%"
  exit /b 2
)
if defined ANYFAIL exit /b 1
exit /b 0
