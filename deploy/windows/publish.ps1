#requires -Version 7
<#
.SYNOPSIS
  Build the SPA, publish the Laplace API, inject env config into web.config, and
  sync into the IIS site folder. Does NOT need elevation (file build only) —
  Install-LaplaceSite.ps1 does the one-time elevated IIS wiring.
#>
[CmdletBinding()]
param(
  [string]$RepoRoot      = (Resolve-Path "$PSScriptRoot\..\.."),
  [string]$OutDir        = "C:\inetpub\laplace-api",
  [string]$EnvFile       = "$PSScriptRoot\laplace-api.env",
  [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$proj = "$RepoRoot\app\Laplace.Endpoints.OpenAICompat\Laplace.Endpoints.OpenAICompat.csproj"
if (-not (Test-Path $EnvFile)) {
  Write-Warning "No $EnvFile — using laplace-api.env.example (edit it for real config)."
  $EnvFile = "$PSScriptRoot\laplace-api.env.example"
}

Write-Host "==> [1/5] build front-end (web/ -> dist)" -ForegroundColor Cyan
Push-Location "$RepoRoot\web"
try { npm ci; if ($LASTEXITCODE) { throw "npm ci failed" }; npm run build; if ($LASTEXITCODE) { throw "npm build failed" } }
finally { Pop-Location }

Write-Host "==> [2/5] publish API -> staging" -ForegroundColor Cyan
$stage = Join-Path $env:TEMP "laplace-api-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
dotnet publish $proj -c $Configuration --no-self-contained -o $stage
if ($LASTEXITCODE) { throw "dotnet publish failed" }

Write-Host "==> [3/5] overlay SPA into wwwroot + ensure native DLLs" -ForegroundColor Cyan
$wwwroot = Join-Path $stage "wwwroot"
if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
New-Item -ItemType Directory $wwwroot | Out-Null
Copy-Item "$RepoRoot\web\dist\*" $wwwroot -Recurse -Force
# Belt-and-suspenders: Directory.Build.targets copies these on Build, but make
# sure the engine + libxml native deps sit next to the published app.
$natives = @(
  "$RepoRoot\build-win\core\laplace_core.dll",
  "$RepoRoot\build-win\dynamics\laplace_dynamics.dll",
  "$RepoRoot\build-win\synthesis\laplace_synthesis.dll",
  "C:\Program Files\PostgreSQL\18\bin\libxml2.dll"
)
foreach ($n in $natives) { if (Test-Path $n) { Copy-Item $n $stage -Force } else { Write-Warning "native dep missing: $n" } }

Write-Host "==> [4/5] inject env config into web.config" -ForegroundColor Cyan
$webConfig = Join-Path $stage "web.config"
[xml]$xml = Get-Content $webConfig
$aspNetCore = $xml.SelectSingleNode("//aspNetCore")
if (-not $aspNetCore) { throw "web.config has no <aspNetCore> (not an in-process publish?)" }
$envNode = $aspNetCore.SelectSingleNode("environmentVariables")
if ($envNode) { [void]$aspNetCore.RemoveChild($envNode) }
$envNode = $xml.CreateElement("environmentVariables")
Get-Content $EnvFile | ForEach-Object {
  $line = $_.Trim()
  if ($line -and -not $line.StartsWith("#") -and $line.Contains("=")) {
    $k, $v = $line -split "=", 2
    $e = $xml.CreateElement("environmentVariable")
    $e.SetAttribute("name", $k.Trim()); $e.SetAttribute("value", $v.Trim())
    [void]$envNode.AppendChild($e)
  }
}
[void]$aspNetCore.AppendChild($envNode)
$xml.Save($webConfig)

Write-Host "==> [5/5] sync staging -> $OutDir" -ForegroundColor Cyan
New-Item -ItemType Directory $OutDir -Force | Out-Null
# In-process IIS holds an exclusive lock on the managed app DLL while the worker runs,
# so a plain /MIR would fail to overwrite it. Drop app_offline.htm first: ANCM detects it,
# drains + unloads the app, releasing the locks. /XF keeps the mirror from deleting it
# mid-copy; we remove it last so ANCM restarts the freshly-published app.
$offline = Join-Path $OutDir "app_offline.htm"
Set-Content -Path $offline -Value "<h1>Laplace is deploying…</h1>" -Encoding utf8
Start-Sleep -Seconds 1
# robocopy /MIR mirrors; exclude the live logs dir + the offline marker. Exit codes <8 are success.
robocopy $stage $OutDir /MIR /XD logs /XF app_offline.htm /NFL /NDL /NJH /NJS /NP | Out-Null
# robocopy exit codes 0-7 are success (1 = files copied); >=8 is a real error.
$rc = $LASTEXITCODE
Remove-Item $offline -Force -ErrorAction SilentlyContinue
if ($rc -ge 8) { throw "robocopy failed ($rc)" }
$global:LASTEXITCODE = 0
Write-Host "OK published to $OutDir" -ForegroundColor Green
exit 0
