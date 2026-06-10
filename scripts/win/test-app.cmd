@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"
set "Platform="
set "PlatformTarget="
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
  dotnet test "%%P\%%P.csproj" -c Release -v minimal --nologo %* || exit /b 1
)
exit /b %ERRORLEVEL%
