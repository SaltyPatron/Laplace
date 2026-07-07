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
  npm ci; if ($LASTEXITCODE) { throw "npm ci failed" }
  if (-not (Test-Path "openapi\openapi.json")) { throw "web/openapi/openapi.json missing — dotnet build Laplace.Endpoints.OpenAICompat first" }
  Write-Host "    generating src/api/types.gen.ts from openapi/openapi.json"
  npm run gen:api; if ($LASTEXITCODE) { throw "npm gen:api failed" }
  npm run build; if ($LASTEXITCODE) { throw "npm build failed" }
}
finally { Pop-Location }

Write-Host "==> [2/6] publish API -> staging" -ForegroundColor Cyan
$stage = Join-Path $env:TEMP "laplace-api-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
dotnet publish $proj -c $Configuration --no-self-contained -o $stage
if ($LASTEXITCODE) { throw "dotnet publish failed" }

Write-Host "==> [3/6] publish laplace-uci beside API (install-root contract)" -ForegroundColor Cyan
$uciStage = Join-Path $env:TEMP "laplace-uci-stage"
if (Test-Path $uciStage) { Remove-Item $uciStage -Recurse -Force }
dotnet publish $uciProj -c $Configuration --no-self-contained -o $uciStage
if ($LASTEXITCODE) { throw "laplace-uci publish failed" }
Get-ChildItem $uciStage -File | Copy-Item -Destination $stage -Force
$uciExe = Join-Path $stage "laplace-uci.exe"
if (-not (Test-Path $uciExe)) { throw "laplace-uci.exe missing from staging — publish step failed" }

Write-Host "==> [4/6] overlay SPA into wwwroot + ensure native DLLs" -ForegroundColor Cyan
$wwwroot = Join-Path $stage "wwwroot"
if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
New-Item -ItemType Directory $wwwroot | Out-Null
Copy-Item "$RepoRoot\web\dist\*" $wwwroot -Recurse -Force
$natives = @(
  "$engineBuild\core\laplace_core.dll",
  "$engineBuild\dynamics\laplace_dynamics.dll",
  "$engineBuild\synthesis\laplace_synthesis.dll",
  "C:\Program Files\PostgreSQL\18\bin\libxml2.dll"
)
foreach ($n in $natives) {
  if (-not (Test-Path $n)) { throw "native dep missing: $n — run scripts\win\build-engine.cmd first" }
  Copy-Item $n $stage -Force
}

Write-Host "==> [5/6] inject env config into web.config" -ForegroundColor Cyan
$webConfig = Join-Path $stage "web.config"
[xml]$xml = Get-Content $webConfig
$aspNetCore = $xml.SelectSingleNode("//aspNetCore")
if (-not $aspNetCore) { throw "web.config has no <aspNetCore> (not an in-process publish?)" }
$envNode = $aspNetCore.SelectSingleNode("environmentVariables")
if ($envNode) { [void]$aspNetCore.RemoveChild($envNode) }
$envNode = $xml.CreateElement("environmentVariables")
$envVars = [ordered]@{}
$chessLabEnv = Join-Path $RepoRoot "deploy\secrets\chess-lab.env"
$lichessEnv = Join-Path $RepoRoot "deploy\secrets\lichess.env"
# laplace-uci is NOT configured here — ChessLabPaths resolves InstallRoot\laplace-uci.exe.
$skipChessLabKeys = [System.Collections.Generic.HashSet[string]]::new(
  [StringComparer]::OrdinalIgnoreCase
)
[void]$skipChessLabKeys.Add('LAPLACE_UCI')
foreach ($file in @($EnvFile, $chessLabEnv, $lichessEnv)) {
  if (-not (Test-Path $file)) {
    if ($file -eq $chessLabEnv) { Write-Warning "No $chessLabEnv — cutechess/stockfish/qt must be set for chess lab gauntlets." }
    if ($file -eq $lichessEnv) { Write-Warning "No $lichessEnv — set LICHESS_API for Lichess connectivity." }
    continue
  }
  Get-Content $file | ForEach-Object {
    $line = $_.Trim()
    if ($line -and -not $line.StartsWith("#") -and $line.Contains("=")) {
      $k, $v = $line -split "=", 2
      $k = $k.Trim()
      if ($skipChessLabKeys.Contains($k)) { return }
      if (-not $envVars.Contains($k)) { $envVars[$k] = $v.Trim() }
    }
  }
}
foreach ($entry in $envVars.GetEnumerator()) {
  $e = $xml.CreateElement("environmentVariable")
  $e.SetAttribute("name", $entry.Key); $e.SetAttribute("value", $entry.Value)
  [void]$envNode.AppendChild($e)
}
[void]$aspNetCore.AppendChild($envNode)
$xml.Save($webConfig)

Write-Host "==> [6/6] sync staging -> $OutDir" -ForegroundColor Cyan
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
