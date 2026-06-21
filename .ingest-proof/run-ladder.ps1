param(
  [string[]]$Sources
)
# Drives the decomposer ladder one source at a time, faithfully via seed-step.cmd
# (same env the real seed uses). Records per-source timing + the final "done:" line
# into a summary so every decomposer is proven or shown failing.

$ErrorActionPreference = 'Continue'
$root = 'D:\Repositories\Laplace'
$proof = Join-Path $root '.ingest-proof'
$summary = Join-Path $proof 'SUMMARY.tsv'
if (-not (Test-Path $summary)) {
  "source`texit`telapsed_s`tdone_line" | Out-File -Encoding utf8 $summary
}

$i = 1
foreach ($src in $Sources) {
  $idx = '{0:D2}' -f ($i + 10)
  $log = Join-Path $proof "$idx-$src.log"
  "START $src $(Get-Date -Format o)" | Out-File -Encoding utf8 $log
  $sw = [Diagnostics.Stopwatch]::StartNew()
  & cmd /c "`"$root\scripts\win\seed-step.cmd`" $src" 2>&1 | Tee-Object -Append -FilePath $log
  $code = $LASTEXITCODE
  $sw.Stop()
  $el = [math]::Round($sw.Elapsed.TotalSeconds,1)
  $done = (Select-String -Path $log -Pattern '^done:' | Select-Object -Last 1).Line
  if (-not $done) { $done = '(no done line — see log)' }
  "END $src exit=$code elapsed_s=$el $(Get-Date -Format o)" | Tee-Object -Append -FilePath $log | Out-Null
  "$src`t$code`t$el`t$done" | Out-File -Encoding utf8 -Append $summary
  Write-Output "==== $src DONE exit=$code elapsed_s=$el ===="
  $i++
}
Write-Output "LADDER DRIVER COMPLETE"
