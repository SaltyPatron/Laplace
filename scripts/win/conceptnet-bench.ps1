# ConceptNet ingest sandbox benchmark — partial corpus via LAPLACE_INGEST_MAX_UNITS.
#
# One ingest run per invocation by default (single percent cap, single config).
# Use -AllConfigs to run the full config matrix; use -Retries N to retry failures.
#
# KNOWN ISSUE: parallel compose (LAPLACE_INGEST_WORKERS > 1) may crash in native
# GrammarRowIterParseRow until parallel ingest safety is fully verified.
# WORKAROUND: default config is 'serial' (Workers=1), or set LAPLACE_INGEST_WORKERS=1.

# PostgreSQL (Windows): restart only via Services (e.g. Restart-Service postgresql-x64-18)
# or services.msc — do NOT use pg_ctl start/stop; it can corrupt Windows service state.
# Before running this script after a restart, wait until PG accepts connections:
#   Test-NetConnection -ComputerName localhost -Port 5432
# or poll with psql/Npgsql until SELECT 1 succeeds (see wait-for-pg.ps1 in this folder).
param(
    [int] $Percent = 1,
    [long] $TotalRows = 34000000,
    [string] $BenchConfig = 'serial',
    [switch] $AllConfigs,
    [int] $Retries = 0,
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$env:LAPLACE_ROOT = $root
$envScript = Join-Path $PSScriptRoot 'env.cmd'
cmd /c "call `"$envScript`" >nul"
$cli = Join-Path $root 'app\Laplace.Cli\bin\Release\net10.0\Laplace.Cli.exe'
$log = Join-Path $root 'conceptnet-bench-results.csv'

if (-not (Test-Path $cli)) {
    Write-Error "Build CLI first: dotnet build app\Laplace.Cli\Laplace.Cli.csproj -c Release"
}

$benchConfigs = @(
    @{ Name = 'parallel+unordered'; Commit = 'unordered'; Batch = 65536; Workers = 4 },
    @{ Name = 'parallel+serial';    Commit = 'serial';    Batch = 65536; Workers = 4 },
    @{ Name = 'serial';             Commit = 'serial';    Batch = 65536; Workers = 1 }
)

if ($AllConfigs) {
    $configs = $benchConfigs
} else {
    $configs = @($benchConfigs | Where-Object { $_.Name -eq $BenchConfig })
    if ($configs.Count -eq 0) {
        $names = ($benchConfigs | ForEach-Object { $_.Name }) -join ', '
        Write-Error "Unknown -BenchConfig '$BenchConfig'. Valid: $names (or -AllConfigs)"
    }
}

function Write-Log($line) {
    Write-Host $line
    Add-Content -Path $log -Value $line
}

function Test-FatalExit([int]$code) {
    if ($code -eq 0) { return $false }
    # ACCESS_VIOLATION 0xC0000005 and other NTSTATUS-style crash codes
    if ($code -eq -1073741819) { return $true }
    if ($code -lt 0) { return $true }
    return $false
}

if (-not (Test-Path $log)) {
    Write-Log 'timestamp,config,pct,max_units,seconds,exit_code'
}

$cap = [long]([math]::Round($TotalRows * $Percent / 100.0))
$exitCode = 0

foreach ($cfg in $configs) {
    if ($cfg.Commit) { $env:LAPLACE_INGEST_COMMIT_PARALLELISM = $cfg.Commit }
    else { Remove-Item Env:\LAPLACE_INGEST_COMMIT_PARALLELISM -ErrorAction SilentlyContinue }
    $env:LAPLACE_INGEST_BATCH = "$($cfg.Batch)"
    $env:LAPLACE_INGEST_WORKERS = "$($cfg.Workers)"
    $env:LAPLACE_INGEST_MAX_UNITS = "$cap"

    Write-Log "---- $($cfg.Name) @ ${Percent}% ($cap rows) ----"

    if ($WhatIf) {
        Write-Host "[WhatIf] ingest conceptnet --force (workers=$($cfg.Workers), batch=$($cfg.Batch), max_units=$cap)"
        continue
    }

    $attempt = 0
    $procExit = 1
    do {
        $attempt++
        if ($attempt -gt 1) {
            Write-Log "retry $attempt/$($Retries + 1) after exit $procExit"
        }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $cmdLine = "call `"$envScript`" && `"$cli`" ingest conceptnet --force"
        $proc = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $cmdLine `
            -WorkingDirectory $root -Wait -PassThru -NoNewWindow
        $sw.Stop()
        $procExit = $proc.ExitCode
    } while ($procExit -ne 0 -and $attempt -le $Retries)

    $secs = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $ts = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssZ')
    Write-Log "$ts,$($cfg.Name),$Percent,$cap,$secs,$procExit"

    if ($procExit -ne 0) {
        $exitCode = $procExit
        $fatal = Test-FatalExit $procExit
        if ($fatal) {
            Write-Error "Fatal ingest exit $procExit (0x$('{0:X8}' -f [uint32]$procExit)); stopping bench. Use LAPLACE_INGEST_WORKERS=1 or -BenchConfig serial."
        }
        Write-Error "Ingest failed with exit $procExit; stopping bench."
    }
}

if (-not $WhatIf) {
    Write-Log '=== CONCEPTNET BENCH DONE ==='
}

if ($exitCode -ne 0) { exit $exitCode }
