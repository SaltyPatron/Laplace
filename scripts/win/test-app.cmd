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
set "MATCHED="
for %%P in (
  Laplace.Engine.Core.Tests
  Laplace.Engine.Dynamics.Tests
  Laplace.Engine.Synthesis.Tests
  Laplace.Decomposers.Abstractions.Tests
  Laplace.Decomposers.Containers.Abstractions.Tests
  Laplace.SubstrateCRUD.Tests
  Laplace.Ingestion.Tests
  Laplace.Decomposers.Unicode.Tests
  Laplace.Decomposers.Model.Tests
  Laplace.Decomposers.FrameNet.Tests
  Laplace.Decomposers.VerbNet.Tests
  Laplace.Decomposers.SemLink.Tests
  Laplace.Decomposers.PropBank.Tests
  Laplace.Decomposers.OpenSubtitles.Tests
  Laplace.Endpoints.OpenAICompat.Tests
) do (
  set "RUNIT=1"
  if defined FILTER (
    echo %%P | findstr /i /c:"%FILTER%" >nul || set "RUNIT="
  )
  if defined RUNIT (
    set "MATCHED=1"
    dotnet test "%%P\%%P.csproj" -c Release -v minimal --nologo !ARGS! || exit /b 1
  )
)
if defined FILTER if not defined MATCHED (
  echo test-app: no test project matches "%FILTER%"
  exit /b 2
)
exit /b 0
