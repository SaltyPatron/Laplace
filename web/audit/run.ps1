#Requires -Version 7
<#
  Runs the full UI audit: static checks, then live checks against a real host with a temporary
  workload key, then writes audit/STATUS.md. The key is minted with the operator credential from
  the enrolled desktop identity (same path as Publish-IisRuntime.ps1) and revoked in finally.
  Usage: pwsh web/audit/run.ps1 [-Url https://dev.hartonomous.com] [-StaticOnly]
#>
param([string]$Url = 'https://dev.hartonomous.com', [switch]$StaticOnly)
$ErrorActionPreference = 'Stop'
$web = Split-Path $PSScriptRoot
Push-Location $web
try {
    node audit/static.mjs
    if (-not $StaticOnly) {
        $ops = if ($env:LAPLACE_SRC) { Join-Path $env:LAPLACE_SRC 'Laplace-Operations' } else { 'E:\Repositories\Laplace\Laplace-Operations' }
        . "$ops/laplace.env.ps1"
        . "$ops/development/credentials.ps1"
        $config = Read-DevelopmentConfig "$ops/development/installation.json"
        $token = New-DevelopmentBaoSession $config
        try {
            $path = (Get-DevelopmentSecretPaths $config)['laplace-api'] -replace '^kv/', 'kv/data/'
            $operator = (Invoke-DevelopmentBao $config GET $path $token).data.data.operator_token
        } finally { $null = Invoke-DevelopmentBao $config POST 'auth/token/revoke-self' $token @{} }
        if (-not $operator) { throw 'No operator credential from the enrolled desktop identity.' }
        $issued = Invoke-RestMethod "$Url/v1/billing/operator/keys" -Method Post -ContentType application/json `
            -Body '{"tenant":"deployment-verification","label":"ui-audit-run"}' -Headers @{'X-Laplace-Operator-Token' = $operator } -TimeoutSec 30
        Write-Host "Temporary key $($issued.key_prefix) issued for $($issued.tenant)"
        try {
            $env:LAPLACE_AUDIT_URL = $Url; $env:LAPLACE_API_KEY = $issued.api_key; $env:LAPLACE_OPERATOR_TOKEN = $operator
            npx playwright test -c audit/playwright.audit.config.ts
        } finally {
            $null = Invoke-RestMethod "$Url/v1/billing/keys/revoke" -Method Post -ContentType application/json `
                -Body (@{ key_prefix = $issued.key_prefix } | ConvertTo-Json -Compress) -Headers @{ Authorization = "Bearer $($issued.api_key)" } -TimeoutSec 30
            Write-Host "Revoked $($issued.key_prefix)"
            Remove-Item Env:LAPLACE_API_KEY, Env:LAPLACE_OPERATOR_TOKEN -ErrorAction SilentlyContinue
            $operator = $null; $issued = $null
        }
    }
    node audit/report.mjs
} finally { Pop-Location }
