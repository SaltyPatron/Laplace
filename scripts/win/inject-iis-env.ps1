#requires -Version 7
# Inject deploy/windows/laplace-api.env + deploy/secrets/{chess-lab,lichess,stripe,identity}.env
# into a published web.config. Called by scripts/win/publish.cmd.
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$WebConfigPath,
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
  [string]$EnvFile = ""
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $WebConfigPath)) {
  throw "web.config missing: $WebConfigPath"
}
if (-not $EnvFile) {
  $EnvFile = Join-Path $RepoRoot "deploy\windows\laplace-api.env"
  if (-not (Test-Path -LiteralPath $EnvFile)) {
    $EnvFile = Join-Path $RepoRoot "deploy\windows\laplace-api.env.example"
  }
}

[xml]$xml = Get-Content -LiteralPath $WebConfigPath
$aspNetCore = $xml.SelectSingleNode("//aspNetCore")
if (-not $aspNetCore) { throw "web.config has no <aspNetCore>" }
$envNode = $aspNetCore.SelectSingleNode("environmentVariables")
if ($envNode) { [void]$aspNetCore.RemoveChild($envNode) }
$envNode = $xml.CreateElement("environmentVariables")
$envVars = [ordered]@{}
$chessLabEnv = Join-Path $RepoRoot "deploy\secrets\chess-lab.env"
$lichessEnv = Join-Path $RepoRoot "deploy\secrets\lichess.env"
$stripeEnv = Join-Path $RepoRoot "deploy\secrets\stripe.env"
$identityEnv = Join-Path $RepoRoot "deploy\secrets\identity.env"
$skip = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[void]$skip.Add('LAPLACE_UCI')

foreach ($file in @($EnvFile, $chessLabEnv, $lichessEnv, $stripeEnv, $identityEnv)) {
  if (-not (Test-Path -LiteralPath $file)) {
    if ($file -eq $chessLabEnv) {
      Write-Verbose "No custom chess-lab.env; using the managed chess runtime"
    }
    if ($file -eq $lichessEnv) {
      Write-Warning "No $lichessEnv — put LICHESS_API in repo .env (publish-deploy syncs it)"
    }
    if ($file -eq $stripeEnv) {
      Write-Warning "No $stripeEnv — put STRIPE_API_SECRET in repo .env (publish-deploy syncs it)"
    }
    if ($file -eq $identityEnv) {
      Write-Verbose "No $identityEnv — Microsoft and Google sign-in stay disabled"
    }
    continue
  }
  Get-Content -LiteralPath $file | ForEach-Object {
    $line = $_.Trim()
    if ($line -and -not $line.StartsWith("#") -and $line.Contains("=")) {
      $k, $v = $line -split "=", 2
      $k = $k.Trim()
      if ($skip.Contains($k)) { return }
      if (-not $envVars.Contains($k)) { $envVars[$k] = $v.Trim() }
    }
  }
}

# Explicit deployment configuration takes precedence over shared source/build paths.
$external = if ($env:LAPLACE_EXTERNAL) { $env:LAPLACE_EXTERNAL } else { Join-Path $RepoRoot 'external' }
$cuteBuild = if ($env:LAPLACE_CUTECHESS_BUILD) { $env:LAPLACE_CUTECHESS_BUILD } else { 'D:\Data\Laplace\build-cutechess' }
$stockfishSource = if ($env:LAPLACE_STOCKFISH_SOURCE) { $env:LAPLACE_STOCKFISH_SOURCE } else { Join-Path $external 'stockfish' }
$managedChess = [ordered]@{
  LAPLACE_CUTECHESS = Join-Path $cuteBuild 'cutechess-cli.exe'
  LAPLACE_STOCKFISH = Join-Path $stockfishSource 'src\stockfish.exe'
  LAPLACE_QT_BIN = $cuteBuild
  LAPLACE_ZSTD_LIBRARY = Join-Path (Split-Path -Parent $WebConfigPath) 'libzstd.dll'
}
foreach ($key in @('LAPLACE_ZSTD_LIBRARY', 'LAPLACE_ZSTD_WINDOW_LOG_MAX')) {
  $value = [Environment]::GetEnvironmentVariable($key, 'Process')
  if (-not [string]::IsNullOrWhiteSpace($value)) { $envVars[$key] = $value.Trim() }
}
foreach ($entry in $managedChess.GetEnumerator()) {
  if (-not $envVars.Contains($entry.Key)) { $envVars[$entry.Key] = $entry.Value }
}

foreach ($entry in $envVars.GetEnumerator()) {
  $e = $xml.CreateElement("environmentVariable")
  $e.SetAttribute("name", $entry.Key)
  $e.SetAttribute("value", $entry.Value)
  [void]$envNode.AppendChild($e)
}
[void]$aspNetCore.AppendChild($envNode)
$xml.Save($WebConfigPath)
Write-Host "[inject-iis-env] $($envVars.Count) vars -> $WebConfigPath"
