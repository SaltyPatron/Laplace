# Phase 8 — the proof. Fresh DB, run each decomposer one-by-one in layer order, measured.
# Captures per-source elapsed + rows/s + peak client RSS + readback. ConceptNet only with -IncludeConceptNet.
param(
  [switch]$IncludeConceptNet,
  [switch]$NoReset,
  [string[]]$Sources
)
$ErrorActionPreference = 'Continue'
$root  = 'D:\Repositories\Laplace'
$proof = Join-Path $root '.ingest-proof'
$cli   = "$root\scripts\win\cli.cmd"
$psql  = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$env:PGPASSWORD = 'postgres'
$summary = Join-Path $proof 'PHASE8-SUMMARY.tsv'
if (-not $NoReset -or -not (Test-Path $summary)) {
  "source`texit`telapsed_s`tpeak_rss_gb`treadback" | Out-File -Encoding utf8 $summary
}

$ladder = @('unicode','iso639','wordnet','omw','verbnet','propbank','framenet','semlink','atomic2020','ud','wiktionary','tatoeba','opensubtitles')
if ($IncludeConceptNet) { $ladder += 'conceptnet' }
if ($Sources) { $ladder = $Sources }

if (-not $NoReset) {
  Write-Output "==== PHASE 8: db-reset ===="
  & cmd /c "`"$root\scripts\win\db-reset.cmd`"" *>&1 | Select-Object -Last 2
}

$i = 0
foreach ($src in $ladder) {
  $i++; $idx = '{0:D2}' -f $i
  $log = Join-Path $proof "p8-$idx-$src.log"
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $p = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c',"`"$cli`" ingest $src" `
        -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err"
  $peak = 0.0
  while (-not $p.HasExited) {
    $c = Get-Process Laplace.Cli -ErrorAction SilentlyContinue
    if ($c) { $ws = ($c | Measure-Object WorkingSet64 -Sum).Sum/1GB; if ($ws -gt $peak) { $peak = $ws } }
    Start-Sleep -Milliseconds 700
  }
  $sw.Stop()
  $code = $p.ExitCode
  $el = [math]::Round($sw.Elapsed.TotalSeconds,1)
  $pk = [math]::Round($peak,2)
  $rb = (Select-String -Path $log -Pattern 'layer_complete=|^\s+check ' | Select-Object -Last 1).Line
  if ($rb) { $rb = ($rb -replace '\s+',' ').Trim() } else { $rb = '(no readback)' }
  "$src`t$code`t$el`t$pk`t$rb" | Out-File -Encoding utf8 -Append $summary
  Write-Output ("==== {0,-13} exit={1} elapsed_s={2,-7} peak_rss_gb={3} ====" -f $src,$code,$el,$pk)
  if ($code -ne 0) { Write-Output "  STOP: $src failed (exit $code) — see $log"; break }
}

Write-Output "==== SUBSTRATE AUDIT ===="
& $psql -h localhost -U postgres -d laplace -t -A -c "SELECT metric || '=' || value FROM laplace.substrate_counts();" 2>&1
Write-Output "==== PHASE 8 SUMMARY ===="
Get-Content $summary
