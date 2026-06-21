# wait for the current ladder (ud) Cli to exit, then run the rest incl ConceptNet last
while (Get-Process Laplace.Cli -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 5 }
Start-Sleep -Seconds 3
& "C:\Program Files\PowerShell\7\pwsh.exe" -NoProfile -Command "& 'D:\Repositories\Laplace\.ingest-proof\phase8-proof.ps1' -NoReset -Sources wiktionary,tatoeba,opensubtitles,conceptnet" *>&1 |
  Tee-Object 'D:\Repositories\Laplace\.ingest-proof\phase8-rest.out'
