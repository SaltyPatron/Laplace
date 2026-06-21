# Decisive test: does concurrent insertion into a LIVE GiST-indexed table scale?
# 2M synthetic physicalities, partitioned by rn % K, K writers each on its own backend.
$env:PGPASSWORD='postgres'
$psql='C:\Program Files\PostgreSQL\18\bin\psql.exe'
$db=@('-h','localhost','-U','postgres','-d','laplace')
$proof='D:\Repositories\Laplace\.ingest-proof'
$cols='id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at'

Write-Output "setup..."
& $psql @db -v ON_ERROR_STOP=1 -f (Join-Path $proof 'parallel-gist-setup.sql') | Out-Null

foreach ($K in 1,4,8) {
  & $psql @db -c "TRUNCATE laplace.bench_par;" | Out-Null
  # write per-worker sql files
  $files=@()
  for ($w=0; $w -lt $K; $w++) {
    $f = Join-Path $proof "par_w_${K}_${w}.sql"
    "SET synchronous_commit=off;`nINSERT INTO laplace.bench_par ($cols) SELECT $cols FROM laplace.bench_par_src WHERE rn % $K = $w ON CONFLICT DO NOTHING;" | Set-Content -Encoding ascii $f
    $files += $f
  }
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $procs=@()
  foreach ($f in $files) {
    $procs += Start-Process -FilePath $psql -ArgumentList ($db + @('-q','-v','ON_ERROR_STOP=1','-f',$f)) -PassThru -NoNewWindow -RedirectStandardError ($f + '.err')
  }
  $procs | Wait-Process
  $sw.Stop()
  $n = (& $psql @db -t -A -c "SELECT count(*) FROM laplace.bench_par;").Trim()
  $rate = if ($sw.Elapsed.TotalSeconds -gt 0) { [math]::Round([int64]$n/$sw.Elapsed.TotalSeconds,0) } else { 0 }
  Write-Output ("K={0,-2} writers  secs={1,-7} rows={2}  aggregate_rows_per_s={3}" -f $K,[math]::Round($sw.Elapsed.TotalSeconds,2),$n,$rate)
}
& $psql @db -c "DROP TABLE IF EXISTS laplace.bench_par; DROP TABLE IF EXISTS laplace.bench_par_src;" | Out-Null
Write-Output "PARALLEL GiST TEST COMPLETE"
