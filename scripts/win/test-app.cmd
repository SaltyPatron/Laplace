@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
rem Golden/billing gate tests must not inherit dev bypass from env.cmd
set "LAPLACE_BILLING_BYPASS="
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
if not defined XUNIT_TIER_EXCLUDE set "XUNIT_TIER_EXCLUDE=Tier^!=perf"

rem Semicolon-separated list for PowerShell (spaces in ARGS stay separate).
set "PROJECTS="
for %%P in (
  Laplace.Core.Tests
  Laplace.Substrate.Tests
  Laplace.Decomposers.Tests
  Laplace.Endpoints.OpenAICompat.Tests
  Laplace.Chess.Tests
) do (
  set "RUNIT=1"
  if defined FILTER (
    echo %%P | findstr /i /c:"%FILTER%" >nul || set "RUNIT="
  )
  if defined RUNIT (
    set "MATCHED=1"
    if defined PROJECTS (
      set "PROJECTS=!PROJECTS!;%%P"
    ) else (
      set "PROJECTS=%%P"
    )
  )
)
if defined FILTER if not defined MATCHED (
  echo test-app: no test project matches "%FILTER%"
  exit /b 2
)

if defined LAPLACE_TEST_SERIAL goto serial_run

echo test-app: running projects in parallel: !PROJECTS!
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$app = '%CD%';" ^
  "$filter = '!XUNIT_TIER_EXCLUDE!';" ^
  "$extra = '%ARGS%'.Trim();" ^
  "$projects = '%PROJECTS%'.Split(';') | Where-Object { $_ };" ^
  "foreach ($p in $projects) {" ^
  "  Write-Host ('==== building ' + $p + ' ====');" ^
  "  & dotnet build (Join-Path $p ($p + '.csproj')) -c Release -v minimal --nologo;" ^
  "  if ($LASTEXITCODE -ne 0) { Write-Host ('FAIL build ' + $p); exit 1 }" ^
  "};" ^
  "$procs = @();" ^
  "foreach ($p in $projects) {" ^
  "  $out = Join-Path $env:TEMP ('laplace-test-' + $p + '.log');" ^
  "  $argList = @('test', (Join-Path $p ($p + '.csproj')), '-c', 'Release', '-v', 'minimal', '--nologo', '--no-build', '--filter', $filter);" ^
  "  if ($extra) { $argList += $extra.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries) };" ^
  "  Write-Host ('==== starting ' + $p + ' ====');" ^
  "  $err = $out + '.err';" ^
  "  $procs += [pscustomobject]@{ Name = $p; Log = $out; Err = $err; Proc = (Start-Process -FilePath 'dotnet' -ArgumentList $argList -WorkingDirectory $app -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err) }" ^
  "};" ^
  "$procs.Proc | Wait-Process;" ^
  "$fail = $false;" ^
  "foreach ($x in $procs) {" ^
  "  Write-Host ('==== ' + $x.Name + ' ====');" ^
  "  Get-Content $x.Log,$x.Err -ErrorAction SilentlyContinue;" ^
  "  if ($x.Proc.ExitCode -ne 0) { Write-Host ('FAIL ' + $x.Name + ' rc=' + $x.Proc.ExitCode); $fail = $true } else { Write-Host ('PASS ' + $x.Name) }" ^
  "};" ^
  "if ($fail) { exit 1 }; exit 0"
exit /b %ERRORLEVEL%

:serial_run
echo test-app: serial mode (LAPLACE_TEST_SERIAL)
for %%P in ("!PROJECTS:;=" "!") do (
  if not "%%~P"=="" (
    if "!CONTINUE_ON_FAIL!"=="1" (
      dotnet test "%%~P\%%~P.csproj" -c Release -v minimal --nologo --filter "!XUNIT_TIER_EXCLUDE!" !ARGS! || set "ANYFAIL=1"
    ) else (
      dotnet test "%%~P\%%~P.csproj" -c Release -v minimal --nologo --filter "!XUNIT_TIER_EXCLUDE!" !ARGS! || exit /b 1
    )
  )
)
if defined ANYFAIL exit /b 1
exit /b 0
