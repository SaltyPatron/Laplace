# bench-matrix.ps1 — local-vs-wire benchmark driver. Owner: Claude.
#
# Times identical operations against the laplace_bench DB on each host and emits
# a comparison report (CSV + markdown). Axes and cells:
#   setup   : nuke + migrate laplace_bench (Laplace.Migrations), record extversion/schema
#   ingest  : seed-step unicode, seed-step iso639, cli ingest ud <one .conllu>,
#             seed-step chess <one .pgn>   (client = THIS box; wire cost rides COPY/SPI)
#   queries : every "-- name:" cell in scripts\sql\bench-queries.sql, $QueryReps reps,
#             median reported (connection setup included on purpose — it IS wire cost)
#   foundry : optional (-Foundry) synthesis pass; off by default on a bench-sized substrate
#
# Safety rails: operates only on laplace_bench (hard-coded); refuses to start while a
# Laplace.Cli ingest is live (one-ingest-at-a-time law); cells run strictly serial.
# Client-placement axis (CLI running ON hart-server) is v2 — this measures DB placement.

[CmdletBinding()]
param(
    [string[]]$BenchHosts = @('localhost', 'hart-server'),
    [switch]$SkipIngest,
    # Export cell: pass the full CLI argument string, e.g.
    #   -FoundryArgs 'synthesize substrate D:\Data\recipes\bench.json {OUT}\bench.gguf'
    # {OUT} expands to the report directory. Off when empty (bench-sized substrates
    # rarely justify a synthesis pass).
    [string]$FoundryArgs = '',
    [string]$UdFile,
    [string]$Pgn = 'D:\Data\Ingest\Games\Chess\Anthony-Hart_chesscom.pgn',
    [int]$QueryReps = 5
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$PsqlExe  = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$MigDll   = 'D:\Data\Laplace\app\bin\Laplace.Migrations\Release\net10.0\Laplace.Migrations.dll'
$BenchDb  = 'laplace_bench'   # the ONLY database this script may touch
$Stamp    = Get-Date -Format 'yyyyMMdd-HHmmss'
$OutDir   = "D:\Data\Output\bench\$Stamp"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$Results  = [System.Collections.Generic.List[object]]::new()

function Get-HostUser([string]$h) { if ($h -eq 'localhost') { 'postgres' } else { 'laplace_admin' } }

function Invoke-Psql([string]$h, [string]$db, [string]$sql) {
    & $PsqlExe -h $h -U (Get-HostUser $h) -d $db -tAX -v ON_ERROR_STOP=1 -c $sql 2>&1
    if ($LASTEXITCODE -ne 0) { throw "psql($h/$db) failed: $sql" }
}

function Set-BenchEnv([string]$h) {
    $u = Get-HostUser $h
    $env:LAPLACE_PGHOST = $h
    $env:LAPLACE_PGUSER = $u
    $env:LAPLACE_DBNAME = $BenchDb
    $env:LAPLACE_DB     = "Host=$h;Username=$u;Password=postgres;Database=$BenchDb;Command Timeout=0"
    $env:LAPLACE_ENV_LOADED = $null
}

function Invoke-Timed([string]$h, [string]$phase, [string]$cell, [scriptblock]$body) {
    Write-Host ("[{0}] {1}/{2} ..." -f $h, $phase, $cell)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $note = ''
    try { $note = & $body } catch { $sw.Stop(); throw }
    $sw.Stop()
    $row = [pscustomobject]@{
        Host = $h; Phase = $phase; Cell = $cell
        Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 3)
        Note = "$note"
    }
    $Results.Add($row)
    Write-Host ("[{0}] {1}/{2} = {3}s" -f $h, $phase, $cell, $row.Seconds)
}

function Invoke-WinCmd([string]$commandLine, [string]$logName) {
    # scripts\win\*.cmd must never launch under pwsh's .cmd handling — route via cmd.exe.
    $log = Join-Path $OutDir $logName
    & cmd.exe /d /c "call $commandLine" *>> $log
    if ($LASTEXITCODE -ne 0) { throw "'$commandLine' exit=$LASTEXITCODE — see $log" }
}

# ---- preflight -------------------------------------------------------------
if (-not (Test-Path $PsqlExe)) { throw "psql missing at $PsqlExe" }
$live = Get-CimInstance Win32_Process | Where-Object {
    ($_.Name -eq 'dotnet.exe' -or $_.Name -eq 'Laplace.Cli.exe') -and $_.CommandLine -match 'Laplace\.Cli'
}
if ($live) { throw 'a Laplace.Cli ingest is already running — one ingest at a time' }
if (-not (Test-Path $MigDll)) {
    Write-Host 'building Laplace.Migrations...'
    dotnet build (Join-Path $RepoRoot 'app\Laplace.Migrations\Laplace.Migrations.csproj') -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Laplace.Migrations build failed' }
}
if (-not $SkipIngest) {
    if (-not $UdFile) {
        $UdFile = Get-ChildItem 'D:\Data\Ingest\UD-Treebanks' -Recurse -Filter '*.conllu' -ErrorAction Stop |
                  Sort-Object Length | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not (Test-Path $UdFile)) { throw "UD file missing: $UdFile" }
    if (-not (Test-Path $Pgn))    { throw "PGN missing: $Pgn" }
}

# metadata: wire RTT + generations, recorded so every report is self-describing
$meta = [ordered]@{ timestamp = $Stamp; udFile = $UdFile; pgn = $Pgn; queryReps = $QueryReps }
foreach ($h in $BenchHosts) {
    if ($h -ne 'localhost') {
        $ping = Test-Connection $h -Count 5 -ErrorAction SilentlyContinue
        $meta["rtt_ms_$h"] = if ($ping) { [math]::Round(($ping | Measure-Object -Property Latency -Average).Average, 2) } else { 'unreachable' }
    }
}

# ---- matrix ----------------------------------------------------------------
foreach ($h in $BenchHosts) {
    Set-BenchEnv $h

    if (-not $SkipIngest) {
        Invoke-Timed $h 'setup' 'nuke+migrate' {
            dotnet $MigDll nuke --yes --database $BenchDb | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "migrations nuke failed on $h" }
            dotnet $MigDll up --database $BenchDb | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "migrations up failed on $h" }
        }
    }

    $ext = Invoke-Psql $h $BenchDb "SELECT extversion FROM pg_extension WHERE extname='laplace_substrate'"
    $kind = Invoke-Psql $h $BenchDb "SELECT c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='laplace' AND c.relname='entities'"
    $meta["extversion_$h"] = "$ext"
    if ("$kind" -ne 'p') { throw "$h laplace_bench is not the partitioned generation (relkind=$kind) — deploy/rebuild extension first" }

    if (-not $SkipIngest) {
        Invoke-Timed $h 'ingest' 'unicode' { Invoke-WinCmd "scripts\win\seed-step.cmd unicode" "$h-unicode.log" }
        Invoke-Timed $h 'ingest' 'iso639'  { Invoke-WinCmd "scripts\win\seed-step.cmd iso639"  "$h-iso639.log" }
        Invoke-Timed $h 'ingest' 'ud-file' { Invoke-WinCmd "scripts\win\cli.cmd ingest ud `"$UdFile`"" "$h-ud.log" }
        Invoke-Timed $h 'ingest' 'chess-pgn' { Invoke-WinCmd "scripts\win\seed-step.cmd chess `"$Pgn`"" "$h-chess.log" }
    }

    # read-side cells from bench-queries.sql, median of $QueryReps
    $qFile = Join-Path $RepoRoot 'scripts\sql\bench-queries.sql'
    $cells = @{}; $name = $null; $buf = @()
    foreach ($line in Get-Content $qFile) {
        if ($line -match '^--\s*name:\s*(\S+)') {
            if ($name -and $buf) { $cells[$name] = ($buf -join "`n") }
            $name = $Matches[1]; $buf = @()
        } elseif ($line -notmatch '^\s*--' -and $line.Trim()) { $buf += $line }
    }
    if ($name -and $buf) { $cells[$name] = ($buf -join "`n") }

    foreach ($cell in ($cells.Keys | Sort-Object)) {
        $times = @()
        foreach ($i in 1..$QueryReps) {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            Invoke-Psql $h $BenchDb $cells[$cell] | Out-Null
            $sw.Stop()
            $times += $sw.Elapsed.TotalSeconds
        }
        $median = ($times | Sort-Object)[[math]::Floor($times.Count / 2)]
        $Results.Add([pscustomobject]@{
            Host = $h; Phase = 'query'; Cell = $cell
            Seconds = [math]::Round($median, 4)
            Note = "median of $QueryReps"
        })
        Write-Host ("[{0}] query/{1} = {2}s (median)" -f $h, $cell, [math]::Round($median, 4))
    }

    if ($FoundryArgs) {
        $fa = $FoundryArgs.Replace('{OUT}', $OutDir)
        Invoke-Timed $h 'export' 'foundry-synthesize' {
            Invoke-WinCmd "scripts\win\cli.cmd $fa" "$h-foundry.log"
        }
    }
}

# ---- report ----------------------------------------------------------------
$csv = Join-Path $OutDir 'bench.csv'
$Results | Export-Csv $csv -NoTypeInformation

$md = [System.Text.StringBuilder]::new()
[void]$md.AppendLine("# Bench $Stamp — local vs wire")
[void]$md.AppendLine('')
foreach ($k in $meta.Keys) { [void]$md.AppendLine("- $k`: $($meta[$k])") }
[void]$md.AppendLine('')
[void]$md.AppendLine('| Phase | Cell | ' + ($BenchHosts -join ' (s) | ') + ' (s) | ratio |')
[void]$md.AppendLine('|---|---|' + ('---|' * ($BenchHosts.Count + 1)))
$byCell = $Results | Group-Object Phase, Cell
foreach ($g in $byCell) {
    $vals = @{}
    foreach ($r in $g.Group) { $vals[$r.Host] = $r.Seconds }
    $cols = foreach ($h in $BenchHosts) { if ($vals.ContainsKey($h)) { $vals[$h] } else { '-' } }
    $ratio = '-'
    if ($BenchHosts.Count -eq 2 -and $vals.Count -eq 2 -and $vals[$BenchHosts[0]] -gt 0) {
        $ratio = [math]::Round($vals[$BenchHosts[1]] / $vals[$BenchHosts[0]], 2)
    }
    $first = $g.Group[0]
    [void]$md.AppendLine("| $($first.Phase) | $($first.Cell) | " + ($cols -join ' | ') + " | $ratio |")
}
[void]$md.AppendLine('')
[void]$md.AppendLine('ratio = second host / first host; >1 means the wire (or that host) is slower.')
if ($meta.Keys -match 'extversion' ) {
    $exts = $BenchHosts | ForEach-Object { $meta["extversion_$_"] } | Select-Object -Unique
    if ($exts.Count -gt 1) {
        [void]$md.AppendLine('')
        [void]$md.AppendLine('**CAVEAT: extension generations differ across hosts — timings are indicative, not parity.**')
    }
}
$sum = Join-Path $OutDir 'summary.md'
$md.ToString() | Set-Content $sum -Encoding utf8

Write-Host ''
Write-Host "report: $sum"
$Results | Format-Table Host, Phase, Cell, Seconds, Note -AutoSize
exit 0
