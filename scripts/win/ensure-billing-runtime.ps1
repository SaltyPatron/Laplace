#requires -Version 7
# Ensure Lichess + Stripe secrets are in deploy/secrets and the Stripe CLI
# webhook forwarder is installed/running. Called from publish-deploy / setup-host.
# No separate human checklist — this IS the billing runtime contract on Windows.
[CmdletBinding()]
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
  [string]$StripeExe = "D:\Stripe\stripe.exe",
  [string]$NssmExe = "D:\NSSM\nssm-2.24\win64\nssm.exe",
  [string]$ServiceName = "LaplaceStripeListen",
  [string]$DeviceName = "laplace-win-dev",
  [string]$ForwardTo = "http://127.0.0.1:5187/v1/billing/webhooks/stripe",
  [string]$LogDir = "D:\Data\Output",
  [switch]$RequireService
)
$ErrorActionPreference = "Stop"

function Test-IsAdmin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  return [Security.Principal.WindowsPrincipal]::new($id).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$sync = Join-Path $PSScriptRoot "sync-operator-secrets.ps1"
if (-not (Test-Path -LiteralPath $sync)) { throw "missing $sync" }

# 1) Always materialize deploy/secrets from repo .env (and print-secret when possible).
& $sync -RepoRoot $RepoRoot -StripeExe $StripeExe

$isAdmin = Test-IsAdmin
$stripeOk = Test-Path -LiteralPath $StripeExe
$nssmOk = Test-Path -LiteralPath $NssmExe
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

# 2) Stripe listen service: install if admin+missing; start if present.
if (-not $stripeOk) {
  Write-Warning "[ensure-billing-runtime] $StripeExe missing — Checkout works once STRIPE_API_SECRET is synced; webhooks need the CLI."
  if ($RequireService) { exit 1 }
  exit 0
}

if ($null -eq $svc) {
  if (-not $nssmOk) {
    Write-Warning "[ensure-billing-runtime] $NssmExe missing — cannot install $ServiceName"
    if ($RequireService) { exit 1 }
    exit 0
  }
  if (-not $isAdmin) {
    # setup-host.cmd is the elevated one-shot; publish still syncs secrets.
    Write-Warning "[ensure-billing-runtime] $ServiceName not installed — run elevated: scripts\win\setup-host.cmd"
    if ($RequireService) { exit 2 }
    exit 0
  }

  New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
  $stdout = Join-Path $LogDir "stripe-listen.out.log"
  $stderr = Join-Path $LogDir "stripe-listen.err.log"
  & $NssmExe install $ServiceName $StripeExe | Out-Null
  $args = "listen --forward-to $ForwardTo --device-name $DeviceName"
  & $NssmExe set $ServiceName AppDirectory (Split-Path -Parent $StripeExe) | Out-Null
  & $NssmExe set $ServiceName AppParameters $args | Out-Null
  & $NssmExe set $ServiceName DisplayName "Laplace Stripe Listen (dev webhooks)" | Out-Null
  & $NssmExe set $ServiceName Description "Forwards Stripe test webhooks to $ForwardTo" | Out-Null
  & $NssmExe set $ServiceName Start SERVICE_AUTO_START | Out-Null
  & $NssmExe set $ServiceName AppStdout $stdout | Out-Null
  & $NssmExe set $ServiceName AppStderr $stderr | Out-Null
  & $NssmExe set $ServiceName AppRotateFiles 1 | Out-Null
  & $NssmExe set $ServiceName AppRotateBytes 1048576 | Out-Null
  & $NssmExe set $ServiceName AppExit Default Restart | Out-Null
  & $NssmExe set $ServiceName AppRestartDelay 5000 | Out-Null
  & $NssmExe start $ServiceName | Out-Null
  Start-Sleep -Seconds 2
  Write-Host "[ensure-billing-runtime] installed + started $ServiceName"
} else {
  if ($svc.Status -ne "Running") {
    if ($isAdmin) {
      & $NssmExe start $ServiceName | Out-Null
      Start-Sleep -Seconds 1
      Write-Host "[ensure-billing-runtime] started $ServiceName"
    } else {
      Write-Warning "[ensure-billing-runtime] $ServiceName is $($svc.Status) — start needs elevation (setup-host.cmd)"
      if ($RequireService) { exit 2 }
    }
  } else {
    Write-Host "[ensure-billing-runtime] $ServiceName running"
  }
}

# 3) Refresh whsec now that listen device exists.
& $sync -RepoRoot $RepoRoot -StripeExe $StripeExe
$stripeEnv = Join-Path $RepoRoot "deploy\secrets\stripe.env"
$hasWhsec = $false
if (Test-Path -LiteralPath $stripeEnv) {
  foreach ($line in Get-Content -LiteralPath $stripeEnv) {
    if ($line -match '^\s*STRIPE_WEBHOOK_SECRET\s*=\s*whsec_') { $hasWhsec = $true; break }
  }
}
if (-not $hasWhsec) {
  Write-Warning "[ensure-billing-runtime] STRIPE_WEBHOOK_SECRET still missing after sync"
  if ($RequireService) { exit 1 }
}

Write-Host "[ensure-billing-runtime] OK"
exit 0
