#Requires -Version 7
[CmdletBinding()]
param([string]$Installation,[string]$ReceiptDirectory)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../../scripts/win/machine-env.ps1"
. "$PSScriptRoot/iis-runtime.ps1"
if(-not $Installation){$Installation=$env:LAPLACE_IIS_INSTALLATION}
if(-not $Installation){throw 'Declare the IIS installation with LAPLACE_IIS_INSTALLATION or -Installation.'}
$c=Read-IisInstallation $Installation
$web=[IO.Path]::GetFullPath((Join-Path $c.directory 'web'))
$offline=Join-Path $web 'app_offline.htm'
$origin=$c.origin.TrimEnd('/')
if(-not $ReceiptDirectory){$ReceiptDirectory=Join-Path $env:LAPLACE_WORK 'operations'}
$null=New-Item -ItemType Directory -Path $ReceiptDirectory -Force
$receiptFile=Join-Path $ReceiptDirectory ('iis-restart-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json')
$receipt=[ordered]@{site=$c.origin;directory=$web;started_utc=[DateTime]::UtcNow.ToString('o');method='app_offline';offline_observed=$false;offline_removed=$false;website_verified=$false}
$lock=$null;$markerStream=$null;$created=$false
$token='laplace-restart-'+[Guid]::NewGuid().ToString('N')
$marker="<!doctype html><html><body>Application restarting. <!-- $token --></body></html>"
try {
    $lock=[IO.File]::Open((Join-Path $web '.publication.lock'),'OpenOrCreate','ReadWrite','None')
    try {
        # CreateNew preserves any existing deployment's offline marker.
        $markerStream=[IO.File]::Open($offline,'CreateNew','Write','Read')
        $created=$true
        $bytes=[Text.Encoding]::UTF8.GetBytes($marker)
        $markerStream.Write($bytes,0,$bytes.Length)
        $markerStream.Flush($true)
        $markerStream.Dispose()
        $markerStream=$null
        for($attempt=0;$attempt -lt 15;$attempt++){
            $response=Invoke-WebRequest $origin -NoProxy -SkipHttpErrorCheck -TimeoutSec 10
            if($response.Content.Contains($token)){$receipt.offline_observed=$true;break}
            Start-Sleep -Milliseconds 200
        }
        if(-not $receipt.offline_observed){throw 'IIS did not serve the restart marker; the selected website has not confirmed the restart.'}
    }finally{
        if($markerStream){$markerStream.Dispose()}
        if($created){
            if([IO.File]::ReadAllText($offline) -cne $marker){throw 'The offline marker changed during restart; it was preserved for inspection.'}
            Remove-Item -LiteralPath $offline -ErrorAction Stop
            $receipt.offline_removed=$true
        }
    }
    $health=$null;$lastHealthError=$null
    for($attempt=0;$attempt -lt 15;$attempt++){
        try {
            $health=Invoke-RestMethod "$origin/health" -NoProxy -TimeoutSec 10
            if($health.status -eq 'ok'){break}
        }catch{$lastHealthError=$_.Exception.Message}
        Start-Sleep -Seconds 1
    }
    if($health.status -ne 'ok'){throw "IIS served and cleared the restart marker; the application health check failed: $lastHealthError"}
    $readyResponse=Invoke-WebRequest "$origin/health/ready" -NoProxy -SkipHttpErrorCheck -TimeoutSec 20
    $ready=$readyResponse.Content | ConvertFrom-Json
    $receipt.readiness=$ready
    $receipt.readiness_http_status=[int]$readyResponse.StatusCode
    if($readyResponse.StatusCode -notin 200,503){throw "Unexpected readiness response: HTTP $($readyResponse.StatusCode)."}
    if(-not $ready.substrate_reachable -or -not $ready.perfcache_ready){throw "The application responds, but substrate or native-cache readiness failed: $($ready.detail)"}
    $page=Invoke-WebRequest $origin -NoProxy -TimeoutSec 20
    if($page.StatusCode -ne 200 -or $page.Content -notmatch '<html'){throw 'The website did not return its HTML page.'}
    $receipt.website_verified=$true
    $receipt.http_status=[int]$page.StatusCode
    $receipt.substrate_reachable=$ready.substrate_reachable
    $receipt.perfcache_ready=$ready.perfcache_ready
    $receipt.product_ready=[bool]$ready.ready
    Write-Host "Website verified: $origin (HTTP 200, substrate reachable, native cache ready)."
    if(-not $ready.ready){Write-Host "Product readiness remains false: $($ready.detail)"}
}catch{
    $receipt.error=$_.Exception.Message
    throw
}finally{
    if($lock){$lock.Dispose()}
    $receipt.finished_utc=[DateTime]::UtcNow.ToString('o')
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receiptFile -Encoding utf8
    Write-Host "Receipt: $receiptFile"
}
