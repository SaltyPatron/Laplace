@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"
dotnet build "%LAPLACE_ROOT%\app\Laplace.Cli\Laplace.Cli.csproj" -c Release -v q --nologo || exit /b 1
for %%f in (%*) do (
  echo ==== db-roundtrip %%f ====
  dotnet "%LAPLACE_ROOT%\app\Laplace.Cli\bin\Release\net10.0\Laplace.Cli.dll" db-roundtrip "%%~f" || exit /b 1
)
