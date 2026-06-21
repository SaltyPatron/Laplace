# Clean isolation of LAPLACE_COPY_VALIDATE cost: fresh DB, reseed floor, run atomic2020
# with COPY_VALIDATE=0. Round-trip fix is already compiled in. Compare to the prior
# COPY_VALIDATE=1 baseline (~26k rows/s/batch, 292s full atomic).
$ErrorActionPreference='Continue'
$env:PGPASSWORD='postgres'
$env:LAPLACE_COPY_VALIDATE='0'   # the variable under test (seed-step only sets =1 if undefined)
$proof='D:\Repositories\Laplace\.ingest-proof'
function Step($name,$cmd){
  $log=Join-Path $proof $name
  "START $name $(Get-Date -Format o)" | Out-File $log
  & cmd /c $cmd 2>&1 | Tee-Object -Append -FilePath $log
  "END exit=$LASTEXITCODE $(Get-Date -Format o)" | Tee-Object -Append -FilePath $log | Out-Null
}
Step '30-reset.log'  '"D:\Repositories\Laplace\scripts\win\db-reset.cmd"'
Step '31-unicode.log' '"D:\Repositories\Laplace\scripts\win\seed-step.cmd" unicode'
Step '32-iso639.log'  '"D:\Repositories\Laplace\scripts\win\seed-step.cmd" iso639'
Step '33-atomic-noval.log' '"D:\Repositories\Laplace\scripts\win\seed-step.cmd" atomic2020'
Write-Output "MEASURE COMPLETE"
