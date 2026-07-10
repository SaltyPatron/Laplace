#requires -Version 7
[CmdletBinding()]
param(
  [string]$RepoRoot      = (Resolve-Path "$PSScriptRoot\..\.."),
  [string]$OutDir        = $(if ($env:LAPLACE_IIS_API) { $env:LAPLACE_IIS_API } else { "D:\Data\inetsrv\laplace-api" }),
  [string]$EnvFile       = "$PSScriptRoot\laplace-api.env",
  [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$dataRoot = if ($env:LAPLACE_DATA_ROOT) { $env:LAPLACE_DATA_ROOT } else { "D:\Data\Laplace" }
$engineBuild = if ($env:LAPLACE_ENGINE_BUILD) { $env:LAPLACE_ENGINE_BUILD } else { Join-Path $dataRoot "build-win" }
$proj = "$RepoRoot\app\Laplace.Endpoints.OpenAICompat\Laplace.Endpoints.OpenAICompat.csproj"
$uciProj = "$RepoRoot\app\Laplace.Chess.Uci\Laplace.Chess.Uci.csproj"
if (-not (Test-Path $EnvFile)) {
  Write-Warning "No $EnvFile — using laplace-api.env.example (edit it for real config)."
  $EnvFile = "$PSScriptRoot\laplace-api.env.example"
}

Write-Host "==> [1/6] build front-end (web/ -> dist)" -ForegroundColor Cyan
Push-Location "$RepoRoot\web"
try {
  $lock = Join-Path (Get-Location) "package-lock.json"
  $stamp = Join-Path (Get-Location) "node_modules\.laplace-npm-ci.stamp"
  $needCi = $true
  if ((Test-Path "node_modules") -and (Test-Path $lock) -and (Test-Path $stamp)) {
    $lockHash = (Get-FileHash $lock -Algorithm SHA256).Hash
    $prev = (Get-Content -LiteralPath $stamp -Raw -ErrorAction SilentlyContinue).Trim()
    if ($prev -eq $lockHash) {
      Write-Host "    npm ci skipped (package-lock stamp fresh)" -ForegroundColor DarkGray
      $needCi = $false
    }
  }
  if ($needCi) {
    npm ci; if ($LASTEXITCODE) { throw "npm ci failed" }
    if (Test-Path $lock) {
      New-Item -ItemType Directory -Force -Path "node_modules" | Out-Null
      Set-Content -LiteralPath $stamp -Value (Get-FileHash $lock -Algorithm SHA256).Hash -NoNewline -Encoding ascii
    }
  }
  if (-not (Test-Path "openapi\openapi.json")) { throw "web/openapi/openapi.json missing — dotnet build Laplace.Endpoints.OpenAICompat first" }
  Write-Host "    generating src/api/types.gen.ts from openapi/openapi.json"
  npm run gen:api; if ($LASTEXITCODE) { throw "npm gen:api failed" }
  npm run build; if ($LASTEXITCODE) { throw "npm build failed" }
}
finally { Pop-Location }

Write-Host "==> [2/6] publish API + UCI in parallel -> staging" -ForegroundColor Cyan
$stage = Join-Path $env:TEMP "laplace-api-stage"
$uciStage = Join-Path $env:TEMP "laplace-uci-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $uciStage) { Remove-Item $uciStage -Recurse -Force }
$apiLog = Join-Path $env:TEMP "laplace-publish-api.log"
$uciLog = Join-Path $env:TEMP "laplace-publish-uci-iis.log"
$api = Start-Process -FilePath "dotnet" -ArgumentList @("publish", $proj, "-c", $Configuration, "--no-self-contained", "-o", $stage) -PassThru -NoNewWindow -RedirectStandardOutput $apiLog -RedirectStandardError ($apiLog + ".err")
$uci = Start-Process -FilePath "dotnet" -ArgumentList @("publish", $uciProj, "-c", $Configuration, "--no-self-contained", "-o", $uciStage) -PassThru -NoNewWindow -RedirectStandardOutput $uciLog -RedirectStandardError ($uciLog + ".err")
Wait-Process -Id $api.Id, $uci.Id
Get-Content $apiLog, ($apiLog + ".err"), $uciLog, ($uciLog + ".err") -ErrorAction SilentlyContinue
if ($api.ExitCode -ne 0) { throw "dotnet publish API failed (rc=$($api.ExitCode))" }
if ($uci.ExitCode -ne 0) { throw "laplace-uci publish failed (rc=$($uci.ExitCode))" }
Get-ChildItem $uciStage -File | Copy-Item -Destination $stage -Force
$uciExe = Join-Path $stage "laplace-uci.exe"
if (-not (Test-Path $uciExe)) { throw "laplace-uci.exe missing from staging — publish step failed" }

Write-Host "==> [3/6] overlay SPA into wwwroot + ensure native DLLs" -ForegroundColor Cyan
$wwwroot = Join-Path $stage "wwwroot"
if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
New-Item -ItemType Directory $wwwroot | Out-Null
Copy-Item "$RepoRoot\web\dist\*" $wwwroot -Recurse -Force
$natives = @(
  "$engineBuild\core\laplace_core.dll",
  "$engineBuild\dynamics\laplace_dynamics.dll",
  "$engineBuild\synthesis\laplace_synthesis.dll"
)
foreach ($n in $natives) {
  if (-not (Test-Path $n)) { throw "native dep missing: $n — run scripts\win\build-engine.cmd first" }
  Copy-Item $n $stage -Force
}

Write-Host "==> [4/6] inject env config into web.config" -ForegroundColor Cyan
& "$RepoRoot\scripts\win\inject-iis-env.ps1" -WebConfigPath (Join-Path $stage "web.config") -RepoRoot $RepoRoot -EnvFile $EnvFile
if ($LASTEXITCODE) { throw "inject-iis-env failed" }

Write-Host "==> [5/6] sync staging -> $OutDir" -ForegroundColor Cyan
New-Item -ItemType Directory $OutDir -Force | Out-Null
$appcmd = "$env:windir\system32\inetsrv\appcmd.exe"
$pool = "LaplacePool"
if (Test-Path $appcmd) {
  Write-Host "    stopping IIS app pool $pool" -ForegroundColor DarkGray
  & $appcmd stop apppool "/apppool.name:$pool" 2>$null | Out-Null
  $deadline = (Get-Date).AddSeconds(60)
  while ((Get-Date) -lt $deadline) {
    $running = & $appcmd list wp 2>$null | Select-String -Pattern 'LaplacePool' -Quiet
    if (-not $running) { break }
    Start-Sleep -Seconds 1
  }
  if (& $appcmd list wp 2>$null | Select-String -Pattern 'LaplacePool' -Quiet) {
    throw "LaplacePool worker still running — cannot sync to $OutDir"
  }
}
$offline = Join-Path $OutDir "app_offline.htm"
Set-Content -Path $offline -Value "<h1>Laplace is deploying…</h1>" -Encoding utf8
Start-Sleep -Seconds 1
robocopy $stage $OutDir /MIR /XD logs /XF app_offline.htm /NFL /NDL /NJH /NJS /NP | Out-Null
$rc = $LASTEXITCODE
Remove-Item $offline -Force -ErrorAction SilentlyContinue
if ($rc -ge 8) { throw "robocopy failed ($rc)" }
if (-not (Test-Path (Join-Path $OutDir "laplace-uci.exe"))) { throw "laplace-uci.exe missing from $OutDir after sync" }
$mainDll = Join-Path $OutDir "Laplace.Endpoints.OpenAICompat.dll"
$stageDll = Join-Path $stage "Laplace.Endpoints.OpenAICompat.dll"
if ((Get-FileHash $stageDll).Hash -ne (Get-FileHash $mainDll).Hash) {
  throw "live DLL hash != staged build — copy failed or file locked"
}
if (Test-Path $appcmd) {
  Write-Host "    starting IIS app pool $pool" -ForegroundColor DarkGray
  & $appcmd start apppool "/apppool.name:$pool" 2>$null | Out-Null
  & $appcmd start site "/site.name:Laplace" 2>$null | Out-Null
}
$global:LASTEXITCODE = 0
Write-Host "OK published to $OutDir (laplace-uci.exe at install root)" -ForegroundColor Green
exit 0
