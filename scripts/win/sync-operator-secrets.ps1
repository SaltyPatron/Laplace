#requires -Version 7
# Sync repo-root .env → deploy/secrets/{lichess,stripe}.env (gitignored).
# Optionally refresh STRIPE_WEBHOOK_SECRET from `stripe listen --print-secret`.
[CmdletBinding()]
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
  [string]$EnvFile = "",
  [string]$StripeExe = "D:\Stripe\stripe.exe",
  [switch]$SkipWebhookSecret
)
$ErrorActionPreference = "Stop"
if (-not $EnvFile) { $EnvFile = Join-Path $RepoRoot ".env" }
if (-not (Test-Path -LiteralPath $EnvFile)) {
  throw "Missing $EnvFile — create it with LICHESS_API / STRIPE_API_SECRET"
}

$secretsDir = Join-Path $RepoRoot "deploy\secrets"
New-Item -ItemType Directory -Force -Path $secretsDir | Out-Null

$map = @{}
Get-Content -LiteralPath $EnvFile | ForEach-Object {
  $line = $_.Trim()
  if (-not $line -or $line.StartsWith("#")) { return }
  $eq = $line.IndexOf("=")
  if ($eq -lt 1) { return }
  $name = $line.Substring(0, $eq).Trim()
  $val = $line.Substring($eq + 1).Trim().Trim("'").Trim('"')
  if ($name) { $map[$name] = $val }
}

$lichess = $null
foreach ($k in @("LICHESS_API", "LICHESS_TOKEN")) {
  if ($map.ContainsKey($k) -and -not [string]::IsNullOrWhiteSpace($map[$k])) {
    $lichess = $map[$k]
    break
  }
}
if (-not $lichess) { throw "No LICHESS_API / LICHESS_TOKEN in $EnvFile" }

$stripeSecret = $null
foreach ($k in @("STRIPE_API_SECRET", "LAPLACE_STRIPE_API_KEY")) {
  if ($map.ContainsKey($k) -and -not [string]::IsNullOrWhiteSpace($map[$k])) {
    $stripeSecret = $map[$k]
    break
  }
}
if (-not $stripeSecret) { throw "No STRIPE_API_SECRET in $EnvFile" }

$stripePub = $null
foreach ($k in @("STRIPE_API_Publishable", "STRIPE_API_PUBLISHED", "STRIPE_API_PUBLISHABLE")) {
  if ($map.ContainsKey($k) -and -not [string]::IsNullOrWhiteSpace($map[$k])) {
    $stripePub = $map[$k]
    break
  }
}

$existingWhsec = $null
$stripePath = Join-Path $secretsDir "stripe.env"
if (Test-Path -LiteralPath $stripePath) {
  Get-Content -LiteralPath $stripePath | ForEach-Object {
    if ($_ -match '^\s*STRIPE_WEBHOOK_SECRET\s*=\s*(.+)\s*$') {
      $existingWhsec = $matches[1].Trim().Trim("'").Trim('"')
    }
  }
}

$whsec = $null
if (-not $SkipWebhookSecret -and (Test-Path -LiteralPath $StripeExe)) {
  $tmpOut = Join-Path $env:TEMP "laplace-stripe-print-secret.out"
  $tmpErr = Join-Path $env:TEMP "laplace-stripe-print-secret.err"
  try {
    # --api-key: the CLI's stored login expires (api_key_expired); the .env key is
    # the source of truth and print-secret must not depend on `stripe login` state.
    $p = Start-Process -FilePath $StripeExe -ArgumentList @("listen", "--print-secret", "--api-key", $stripeSecret) `
      -NoNewWindow -Wait -PassThru -RedirectStandardOutput $tmpOut -RedirectStandardError $tmpErr
    if ($p.ExitCode -eq 0 -and (Test-Path -LiteralPath $tmpOut)) {
      foreach ($line in Get-Content -LiteralPath $tmpOut) {
        $t = $line.Trim()
        if ($t.StartsWith("whsec_")) { $whsec = $t; break }
      }
    }
    if (-not $whsec -and (Test-Path -LiteralPath $tmpErr)) {
      $errLine = (Get-Content -LiteralPath $tmpErr | Select-Object -First 1)
      if ($errLine) { Write-Warning "[sync-operator-secrets] stripe listen --print-secret failed: $errLine" }
    }
  } finally {
    Remove-Item -LiteralPath $tmpOut, $tmpErr -Force -ErrorAction SilentlyContinue
  }
}
if (-not $whsec) { $whsec = $existingWhsec }
if (-not $whsec -and $map.ContainsKey("STRIPE_WEBHOOK_SECRET")) {
  $whsec = $map["STRIPE_WEBHOOK_SECRET"]
}

$lichessPath = Join-Path $secretsDir "lichess.env"
@(
  "# Synced by scripts/win/sync-operator-secrets.ps1 — do not commit."
  "LICHESS_API=$lichess"
  "LICHESS_TOKEN=$lichess"
) | Set-Content -LiteralPath $lichessPath -Encoding utf8NoBOM

$stripeLines = @(
  "# Synced by scripts/win/sync-operator-secrets.ps1 — do not commit."
  "STRIPE_API_SECRET=$stripeSecret"
)
if ($stripePub) { $stripeLines += "STRIPE_API_Publishable=$stripePub" }
if ($whsec) { $stripeLines += "STRIPE_WEBHOOK_SECRET=$whsec" }
else { $stripeLines += "# STRIPE_WEBHOOK_SECRET=whsec_...  # install-stripe-listen.cmd or: stripe listen --print-secret" }
$stripeLines | Set-Content -LiteralPath $stripePath -Encoding utf8NoBOM

Write-Host "[sync-operator-secrets] wrote $lichessPath"
Write-Host "[sync-operator-secrets] wrote $stripePath (webhook_secret=$(if ($whsec) { 'set' } else { 'missing' }))"
