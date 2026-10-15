#Requires -Version 7
$ErrorActionPreference='Stop'
$work=Join-Path ([IO.Path]::GetTempPath()) ('laplace-restart-test-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path "$work/site/web","$work/receipts"
$saved=@{}
foreach($item in Get-ChildItem Env:){$saved[$item.Name]=$item.Value}
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
try {
    $env:LAPLACE_MACHINE_ENV="$work/machine.env"
    '# isolated configuration fixture' | Set-Content $env:LAPLACE_MACHINE_ENV
    $env:TEST_RESTART_WEB="$work/site/web"
    $declaration=@{
        version=1;host=$env:COMPUTERNAME.ToLowerInvariant();site='fixture';pool='fixture';role='fixture';revision='abc1234'
        directory="$work/site";publish="$work/publish";bao="$work/bao";machine_env=$env:LAPLACE_MACHINE_ENV
        origin='https://fixture.invalid';address='https://fixture.invalid'
        records=@{api='kv/data/api';identity='kv/data/identity';certificate='kv/data/certificate';chess='kv/data/chess'}
        database=@{host=$env:COMPUTERNAME;port=5432;pool_max=4;authentication='sspi';substrate_database='content';application_database='accounts';substrate_role='content_runtime';application_role='accounts_runtime'}
    }
    $declaration | ConvertTo-Json -Depth 6 | Set-Content "$work/installation.json"
    function Invoke-WebRequest {
        param($Uri,[switch]$NoProxy,[switch]$SkipHttpErrorCheck,$TimeoutSec)
        $path=Join-Path $env:TEST_RESTART_WEB 'app_offline.htm'
        if(Test-Path -LiteralPath $path){
            if($env:TEST_OFFLINE_FAIL -eq '1'){return @{StatusCode=500;Content='startup failure'}}
            return @{StatusCode=503;Content=[IO.File]::ReadAllText($path)}
        }
        if($Uri -like '*/health/ready'){
            $connected=$env:TEST_READY_FAIL -ne '1'
            $seeded=$env:TEST_UNSEEDED -ne '1'
            $ready=$connected -and $seeded
            $detail=if(-not $connected){'database does not exist'}elseif(-not $seeded){'substrate has no entities (unseeded)'}else{$null}
            return @{StatusCode=$(if($ready){200}else{503});Content=(@{ready=$ready;substrate_reachable=$connected;perfcache_ready=$true;detail=$detail} | ConvertTo-Json)}
        }
        @{StatusCode=200;Content='<html>fixture</html>'}
    }
    function Invoke-RestMethod {
        param($Uri,[switch]$NoProxy,$TimeoutSec)
        if(Test-Path -LiteralPath (Join-Path $env:TEST_RESTART_WEB 'app_offline.htm')){throw 'Health checked before removing offline marker.'}
        @{status='ok'}
    }
    function Start-Sleep {param($Seconds,$Milliseconds)}
    $command=Join-Path $PSScriptRoot '../deploy/windows/Restart-IisApplication.ps1'
    $invoke=@{Installation="$work/installation.json";ReceiptDirectory="$work/receipts"}
    function LatestReceipt {Get-Content -LiteralPath (Get-ChildItem "$work/receipts" -Filter '*.json' | Sort-Object Name -Descending | Select-Object -First 1).FullName -Raw | ConvertFrom-Json}
    & $command @invoke
    $receipt=LatestReceipt
    Assert ($receipt.offline_observed -and $receipt.offline_removed -and $receipt.website_verified) 'A restart was not fully observed and verified.'
    Assert (-not(Test-Path "$work/site/web/app_offline.htm")) 'Restart left the application offline.'
    Write-Host 'PASS restart marker observed, removed, and website readiness verified'

    'existing deployment marker' | Set-Content "$work/site/web/app_offline.htm"
    $failed=$false
    try {& $command @invoke}catch{$failed=$true}
    Assert ($failed -and (Get-Content "$work/site/web/app_offline.htm" -Raw).Trim() -eq 'existing deployment marker') 'Another deployment marker was modified.'
    Remove-Item -LiteralPath "$work/site/web/app_offline.htm"
    Write-Host 'PASS existing deployment marker is preserved'

    $env:TEST_OFFLINE_FAIL='1';$failed=$false
    try {& $command @invoke}catch{$failed=$true}
    $receipt=LatestReceipt
    Assert ($failed -and -not $receipt.offline_observed -and $receipt.offline_removed -and -not $receipt.website_verified) 'Unobserved restart incorrectly succeeded or left a marker.'
    Remove-Item Env:TEST_OFFLINE_FAIL
    Write-Host 'PASS unobserved restart fails and cleans up its own marker'

    $env:TEST_READY_FAIL='1';$failed=$false
    try {& $command @invoke}catch{$failed=$true}
    $receipt=LatestReceipt
    Assert ($failed -and $receipt.offline_removed -and -not $receipt.website_verified) 'Failed substrate readiness was reported as success.'
    Remove-Item Env:TEST_READY_FAIL
    Write-Host 'PASS readiness failure remains a failure after application restart'

    $env:TEST_UNSEEDED='1'
    & $command @invoke
    $receipt=LatestReceipt
    Assert ($receipt.website_verified -and -not $receipt.product_ready -and $receipt.readiness_http_status -eq 503) 'Unseeded database was confused with full product readiness or a connectivity failure.'
    Remove-Item Env:TEST_UNSEEDED
    Write-Host 'PASS empty substrate is recorded as serving with product readiness false'

    $publicationLock=[IO.File]::Open("$work/site/web/.publication.lock",'OpenOrCreate','ReadWrite','None')
    try {
        $failed=$false
        try {& $command @invoke}catch{$failed=$true}
        Assert ($failed -and -not(Test-Path "$work/site/web/app_offline.htm")) 'Restart interfered with active publication.'
    }finally{$publicationLock.Dispose()}
    Write-Host 'PASS concurrent publication prevents restart without changing the site'
}finally{
    foreach($item in @(Get-ChildItem Env:)){if(-not $saved.ContainsKey($item.Name)){Remove-Item -LiteralPath ('Env:'+$item.Name)}}
    foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
}
