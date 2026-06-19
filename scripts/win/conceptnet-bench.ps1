# ConceptNet sandbox cap via LAPLACE_INGEST_MAX_UNITS. Default cap 5000 (fast sanity check).
# Compose parallelism: LAPLACE_INGEST_COMPOSE_WORKERS. DB commit: LAPLACE_INGEST_WORKERS (keep 1).
#
# PostgreSQL (Windows): restart only via Services — do NOT use pg_ctl.
param(
    [long] $MaxUnits = 5000,
    [int] $Percent = 0,
    [long] $TotalRows = 34000000,
    [string] $BenchConfig = 'compose4',
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
    @{ Name = 'compose4'; Compose = 4; CommitWorkers = 1; Batch = 65536 },
    @{ Name = 'compose1'; Compose = 1; CommitWorkers = 1; Batch = 65536 }
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

$cap = if ($MaxUnits -gt 0) {
    $MaxUnits
} elseif ($Percent -gt 0) {
    [long]([math]::Round($TotalRows * $Percent / 100.0))
} else {
    $existing = [Environment]::GetEnvironmentVariable('LAPLACE_INGEST_MAX_UNITS')
    $parsed = 0L
    if ($existing -and [long]::TryParse($existing, [ref]$parsed)) { $parsed } else { 5000 }
}
$pctLabel = if ($Percent -gt 0) { "$Percent" } else { 'n/a' }
$exitCode = 0

foreach ($cfg in $configs) {
    Remove-Item Env:\LAPLACE_INGEST_COMMIT_PARALLELISM -ErrorAction SilentlyContinue
    $env:LAPLACE_INGEST_BATCH = "$($cfg.Batch)"
    $env:LAPLACE_INGEST_WORKERS = "$($cfg.CommitWorkers)"
    $env:LAPLACE_INGEST_COMPOSE_WORKERS = "$($cfg.Compose)"
    $env:LAPLACE_INGEST_MAX_UNITS = "$cap"
    $env:LAPLACE_INGEST_COMMIT_ROWS = "4000000"

    Write-Log "---- $($cfg.Name) max_units=$cap compose=$($cfg.Compose) commit_workers=$($cfg.CommitWorkers) ----"

    if ($WhatIf) {
        Write-Host "[WhatIf] ingest conceptnet --force (compose=$($cfg.Compose), commit_workers=$($cfg.CommitWorkers), max_units=$cap)"
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
    Write-Log "$ts,$($cfg.Name),$pctLabel,$cap,$secs,$procExit"

    if ($procExit -ne 0) {
        $exitCode = $procExit
        $fatal = Test-FatalExit $procExit
        if ($fatal) {
            Write-Error "Fatal ingest exit $procExit (0x$('{0:X8}' -f [uint32]$procExit)); stopping bench."
        }
        Write-Error "Ingest failed with exit $procExit; stopping bench."
    }
}

if (-not $WhatIf) {
    Write-Log '=== CONCEPTNET BENCH DONE ==='
}

if ($exitCode -ne 0) { exit $exitCode }
