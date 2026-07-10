#requires -Version 7
# Inject deploy/windows/laplace-api.env + deploy/secrets/{chess-lab,lichess}.env
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
$skip = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[void]$skip.Add('LAPLACE_UCI')

foreach ($file in @($EnvFile, $chessLabEnv, $lichessEnv)) {
  if (-not (Test-Path -LiteralPath $file)) {
    if ($file -eq $chessLabEnv) {
      Write-Warning "No $chessLabEnv — run build-cutechess.cmd and copy deploy/windows/chess-lab.env.example"
    }
    if ($file -eq $lichessEnv) {
      Write-Warning "No $lichessEnv — add LICHESS_TOKEN for Lichess connectivity"
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

foreach ($entry in $envVars.GetEnumerator()) {
  $e = $xml.CreateElement("environmentVariable")
  $e.SetAttribute("name", $entry.Key)
  $e.SetAttribute("value", $entry.Value)
  [void]$envNode.AppendChild($e)
}
[void]$aspNetCore.AppendChild($envNode)
$xml.Save($WebConfigPath)
Write-Host "[inject-iis-env] $($envVars.Count) vars -> $WebConfigPath"
