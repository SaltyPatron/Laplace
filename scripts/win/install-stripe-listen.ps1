#requires -Version 7
# Install / update NSSM service "LaplaceStripeListen" that runs:
#   D:\Stripe\stripe.exe listen --forward-to <endpoint webhook> --device-name laplace-win-dev
# Captures whsec into deploy/secrets/stripe.env (requires elevation for NSSM).
[CmdletBinding()]
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
  [string]$StripeExe = "D:\Stripe\stripe.exe",
  [string]$NssmExe = "D:\NSSM\nssm-2.24\win64\nssm.exe",
  [string]$ServiceName = "LaplaceStripeListen",
  [string]$DeviceName = "laplace-win-dev",
  [string]$ForwardTo = "http://127.0.0.1:5187/v1/billing/webhooks/stripe",
  [string]$LogDir = "D:\Data\Output",
  [switch]$Uninstall
)
$ErrorActionPreference = "Stop"

function Test-IsAdmin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  $p = [Security.Principal.WindowsPrincipal]::new($id)
  return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Path -LiteralPath $StripeExe)) { throw "stripe.exe missing: $StripeExe" }
if (-not (Test-Path -LiteralPath $NssmExe)) { throw "nssm.exe missing: $NssmExe" }

if ($Uninstall) {
  if (-not (Test-IsAdmin)) { throw "Uninstall requires an elevated shell (Administrator)." }
  & $NssmExe stop $ServiceName confirm 2>$null | Out-Null
  & $NssmExe remove $ServiceName confirm 2>$null | Out-Null
  Write-Host "[install-stripe-listen] removed service $ServiceName"
  exit 0
}

if (-not (Test-IsAdmin)) {
  Write-Host "[install-stripe-listen] Elevation required to install the Windows service."
  Write-Host "  Re-run elevated:  scripts\win\install-stripe-listen.cmd"
  exit 2
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$stdout = Join-Path $LogDir "stripe-listen.out.log"
$stderr = Join-Path $LogDir "stripe-listen.err.log"

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
  & $NssmExe stop $ServiceName confirm 2>$null | Out-Null
  Start-Sleep -Seconds 1
} else {
  & $NssmExe install $ServiceName $StripeExe | Out-Null
}

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
$svc = Get-Service -Name $ServiceName
Write-Host "[install-stripe-listen] $ServiceName status=$($svc.Status)"

# Refresh deploy/secrets/stripe.env including webhook signing secret
$sync = Join-Path $PSScriptRoot "sync-operator-secrets.ps1"
& $sync -RepoRoot $RepoRoot -StripeExe $StripeExe
Write-Host "[install-stripe-listen] logs: $stdout / $stderr"
Write-Host "[install-stripe-listen] next: publish-deploy.cmd so IIS picks up STRIPE_WEBHOOK_SECRET"
