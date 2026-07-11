#requires -Version 7
# Push repo-root .env (+ deploy/secrets webhook) → GitHub repository Secrets/Variables.
# That is the deploy contract: CI publish injects them into /opt/laplace/secrets.
# Machine secrets.env is irrelevant to deployment.
[CmdletBinding()]
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
  [string]$Repo = "SaltyPatron/Laplace",
  [string]$EnvFile = "",
  [string]$StripeExe = "D:\Stripe\stripe.exe"
)
$ErrorActionPreference = "Stop"
if (-not $EnvFile) { $EnvFile = Join-Path $RepoRoot ".env" }
if (-not (Test-Path -LiteralPath $EnvFile)) { throw "Missing $EnvFile" }

# Ensure local deploy/secrets are current (incl. whsec when CLI can print it).
$syncLocal = Join-Path $PSScriptRoot "sync-operator-secrets.ps1"
if (Test-Path -LiteralPath $syncLocal) {
  & $syncLocal -RepoRoot $RepoRoot -StripeExe $StripeExe
}

function Read-DotEnv([string]$path) {
  $map = @{}
  if (-not (Test-Path -LiteralPath $path)) { return $map }
  Get-Content -LiteralPath $path | ForEach-Object {
    $line = $_.Trim()
    if (-not $line -or $line.StartsWith("#")) { return }
    $eq = $line.IndexOf("=")
    if ($eq -lt 1) { return }
    $map[$line.Substring(0, $eq).Trim()] = $line.Substring($eq + 1).Trim().Trim("'").Trim('"')
  }
  return $map
}

$root = Read-DotEnv $EnvFile
$stripeFile = Read-DotEnv (Join-Path $RepoRoot "deploy\secrets\stripe.env")
$lichessFile = Read-DotEnv (Join-Path $RepoRoot "deploy\secrets\lichess.env")

function Pick($map, [string[]]$keys) {
  foreach ($k in $keys) {
    if ($map.ContainsKey($k) -and -not [string]::IsNullOrWhiteSpace($map[$k])) { return $map[$k] }
  }
  return $null
}

$lichess = Pick $root @("LICHESS_API", "LICHESS_TOKEN")
if (-not $lichess) { $lichess = Pick $lichessFile @("LICHESS_API", "LICHESS_TOKEN") }
$stripeSecret = Pick $root @("STRIPE_API_SECRET", "LAPLACE_STRIPE_API_KEY")
if (-not $stripeSecret) { $stripeSecret = Pick $stripeFile @("STRIPE_API_SECRET", "LAPLACE_STRIPE_API_KEY") }
$stripePub = Pick $root @("STRIPE_API_Publishable", "STRIPE_API_PUBLISHED", "STRIPE_API_PUBLISHABLE")
if (-not $stripePub) { $stripePub = Pick $stripeFile @("STRIPE_API_Publishable", "STRIPE_API_PUBLISHED", "STRIPE_API_PUBLISHABLE") }
$whsec = Pick $root @("STRIPE_WEBHOOK_SECRET", "LAPLACE_STRIPE_WEBHOOK_SECRET")
if (-not $whsec) { $whsec = Pick $stripeFile @("STRIPE_WEBHOOK_SECRET", "LAPLACE_STRIPE_WEBHOOK_SECRET") }

if (-not $lichess) { throw "No LICHESS_API in .env" }
if (-not $stripeSecret) { throw "No STRIPE_API_SECRET in .env" }

function Set-GhSecret([string]$name, [string]$value) {
  $value | & gh secret set $name -R $Repo
  if ($LASTEXITCODE -ne 0) { throw "gh secret set $name failed" }
  Write-Host "[sync-github-secrets] secret $name = set"
}

function Set-GhVar([string]$name, [string]$value) {
  & gh variable set $name -R $Repo -b $value
  if ($LASTEXITCODE -ne 0) { throw "gh variable set $name failed" }
  Write-Host "[sync-github-secrets] var $name = set"
}

Set-GhSecret "LICHESS_API" $lichess
Set-GhSecret "STRIPE_API_SECRET" $stripeSecret
if ($whsec) { Set-GhSecret "STRIPE_WEBHOOK_SECRET" $whsec }
else { Write-Warning "[sync-github-secrets] STRIPE_WEBHOOK_SECRET missing — signed webhooks will fail on host until set" }
if ($stripePub) { Set-GhVar "STRIPE_API_PUBLISHABLE" $stripePub }

# Drop the misnamed secret if it still exists from an earlier pass.
& gh secret delete LICHESS_TOKEN -R $Repo 2>$null
if ($LASTEXITCODE -eq 0) { Write-Host "[sync-github-secrets] deleted obsolete secret LICHESS_TOKEN" }

Write-Host "[sync-github-secrets] OK — CI publish job will materialize /opt/laplace/secrets"
