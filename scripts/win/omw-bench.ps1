param(
    [int[]] $Percents = @(5, 10),
    [long] $TotalRows = 2676800
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$env:LAPLACE_ROOT = $root
$envScript = Join-Path $PSScriptRoot 'env.cmd'
cmd /c "call `"$envScript`" >nul"
$cli = if ($env:LAPLACE_CLI_EXE) { $env:LAPLACE_CLI_EXE } else { Join-Path 'D:\Data\Laplace' 'app\bin\Laplace.Cli\Release\net10.0\Laplace.Cli.exe' }
$log = Join-Path $root 'omw-bench-results.csv'

if (-not (Test-Path $cli)) {
    Write-Error "Build CLI first: dotnet build app\Laplace.Cli\Laplace.Cli.csproj -c Release"
}

$configs = @(
    @{ Name = 'phased+epoch';   Legacy = ''; Commit = ''; Batch = 2048; Workers = 4 },
    @{ Name = 'phased+serial';  Legacy = ''; Commit = 'serial'; Batch = 2048; Workers = 1 },
    @{ Name = 'legacy+unordered'; Legacy = '1'; Commit = 'unordered'; Batch = 8192; Workers = 4 },
    @{ Name = 'legacy+serial';  Legacy = '1'; Commit = 'serial'; Batch = 8192; Workers = 1 }
)

function Write-Log($line) {
    Write-Host $line
    Add-Content -Path $log -Value $line
}

if (-not (Test-Path $log)) {
    Write-Log 'timestamp,config,pct,max_units,seconds,exit_code'
}

foreach ($pct in $Percents) {
    $cap = [long]([math]::Round($TotalRows * $pct / 100.0))
    foreach ($cfg in $configs) {
        $env:LAPLACE_OMW_LEGACY = $cfg.Legacy
        if ($cfg.Commit) { $env:LAPLACE_INGEST_COMMIT_PARALLELISM = $cfg.Commit }
        else { Remove-Item Env:\LAPLACE_INGEST_COMMIT_PARALLELISM -ErrorAction SilentlyContinue }
        $env:LAPLACE_INGEST_BATCH = "$($cfg.Batch)"
        $env:LAPLACE_INGEST_WORKERS = "$($cfg.Workers)"
        $env:LAPLACE_INGEST_MAX_UNITS = "$cap"

        Write-Log "---- $($cfg.Name) @ ${pct}% ($cap rows) ----"
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $cmdLine = "call `"$envScript`" && `"$cli`" ingest omw --force"
        $proc = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $cmdLine `
            -WorkingDirectory $root -Wait -PassThru -NoNewWindow
        $sw.Stop()
        $secs = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        $ts = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssZ')
        Write-Log "$ts,$($cfg.Name),$pct,$cap,$secs,$($proc.ExitCode)"
    }
}

Write-Log '=== OMW BENCH DONE ==='
