#Requires -Version 7
[CmdletBinding()]
param([string]$Installation,[Parameter(Mandatory)][string]$Package,[switch]$Force)
$ErrorActionPreference='Stop'
if(-not $Installation -and -not $env:LAPLACE_IIS_INSTALLATION){
    if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'installation.json')){
        $Installation=Join-Path $PSScriptRoot 'installation.json'
    }else{
        . "$PSScriptRoot/../../scripts/win/machine-env.ps1"
    }
}
if(-not $Installation){$Installation=$env:LAPLACE_IIS_INSTALLATION}
if(-not $Installation){throw 'No IIS installation is configured. Use -Installation for initial setup.'}
. "$PSScriptRoot/iis-runtime.ps1"
. "$PSScriptRoot/iis-access.ps1"
$c=Read-IisInstallation $Installation
Add-Type -Path "$env:windir/System32/inetsrv/Microsoft.Web.Administration.dll"
function Restart-PublishedIisPool {
    $manager=[Microsoft.Web.Administration.ServerManager]::new()
    try {
        $pool=$manager.ApplicationPools[$c.pool]
        if(-not $pool){throw "The declared IIS pool is missing: $($c.pool)"}
        if($pool.State -eq 'Stopped'){$null=$pool.Start()}else{$null=$pool.Recycle()}
    }finally{$manager.Dispose()}
}
$operatorToken=if($env:LAPLACE_OPERATOR_TOKEN){$env:LAPLACE_OPERATOR_TOKEN}else{''}
$apiKey=if($env:LAPLACE_API_KEY){$env:LAPLACE_API_KEY}else{''}
$devInstallation=if($env:LAPLACE_DEVELOPMENT_INSTALLATION){$env:LAPLACE_DEVELOPMENT_INSTALLATION}else{Join-Path $env:LAPLACE_SRC 'Laplace-Operations/development/installation.json'}
$credHelper=Join-Path (Split-Path -Parent $devInstallation) 'credentials.ps1'
if(-not $operatorToken -and (Test-Path -LiteralPath $devInstallation -PathType Leaf) -and (Test-Path -LiteralPath $credHelper -PathType Leaf)){
    try {
        . $credHelper
        $devConfig=Get-Content -LiteralPath $devInstallation -Raw | ConvertFrom-Json
        $token=New-DevelopmentBaoSession $devConfig
        try {
            $path=(Get-DevelopmentSecretPaths $devConfig)['laplace-api'] -replace '^kv/', 'kv/data/'
            $operatorToken=(Invoke-DevelopmentBao $devConfig GET $path $token).data.data.operator_token
            if($operatorToken){$apiKey=''}
        } finally {
            try { if($token){ $null=Invoke-DevelopmentBao $devConfig POST 'auth/token/revoke-self' $token @{} } }
            catch { Write-Warning ('Temporary publication credential-read token could not be revoked: '+$_.Exception.GetType().Name) }
        }
    } catch {
        if(-not $apiKey){throw}
        Write-Warning ('Enrolled publication identity failed; using the explicitly supplied API key. Failure: '+$_.Exception.GetType().Name)
    }
}
if(-not $apiKey -and -not $operatorToken){throw 'Authenticated publication verification requires an enrolled desktop identity, LAPLACE_OPERATOR_TOKEN, or LAPLACE_API_KEY.'}
$manifest=Test-IisPackage $Package
if($manifest.revision -notmatch '^[a-f0-9]{7,64}$'){throw 'Invalid package revision.'}
foreach($component in @('','cli','uci')) {
    & "$PSScriptRoot/Test-NativeSqlCatalog.ps1" -Library (Join-Path (Join-Path $Package $component) 'laplace_core.dll')
}
$web=Join-Path $c.directory 'web';$target=Join-Path $web ('releases/'+$manifest.revision)
$lock=[IO.File]::Open((Join-Path $web '.publication.lock'),'OpenOrCreate','ReadWrite','None')
try {
    $readers=@(Get-IisRuntimeReaders $c)
    if(Test-Path $target){
        $null=Test-IisPackage $target
        Grant-IisRuntimeReaders $target $readers -Recurse
    }else{
        $null=New-Item -ItemType Directory -Path $target -Force
        Grant-IisRuntimeReaders $target $readers
        Copy-IisArtifactTree $Package $target
        Grant-IisRuntimeReaders $target $readers -Recurse
        $null=Test-IisPackage $target
    }
    $config=Join-Path $web 'web.config';$previous=Get-Content -LiteralPath $config -Raw
    $desired=Get-IisWebConfig $manifest.revision (Join-Path $c.directory 'state/logs')
    $changed=$previous -cne $desired
    try {
        if ($changed) { Write-IisPublicAtomic $config $desired $readers }
        # ANCM hosts the runtime inside the worker. Select a fresh worker after
        # changing its executable, including when returning to the prior release.
        if ($changed -or $Force) { Restart-PublishedIisPool }
        else { Write-Host 'IIS package and configuration unchanged: app pool recycling skipped; verifying the running application.' }
        for($i=0;$i -lt 12;$i++){
            try{$result=Test-IisEndpoint $c -OperatorToken $operatorToken -ApiKey $apiKey;break}catch{if($i -eq 11){throw};Start-Sleep -Seconds 2}
        }
    } catch {
        if ($changed) {
            Write-IisPublicAtomic $config $previous $readers
            Restart-PublishedIisPool
        }
        throw
    }
    $result.revision=$manifest.revision
    $result.activation=if ($changed -or $Force) {'restarted'} else {'reused'}
    $receipt=Join-Path $c.directory ('receipts/publication-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'.json')
    Write-IisAtomic $receipt (Iis-Json $result)
    Write-IisPublicAtomic (Join-Path $web 'active.json') (Iis-Json @{revision=$manifest.revision}) $readers
    Write-Host "Published $($manifest.revision) at $($c.origin); authenticated SQL and native storage verified. Receipt: $receipt"
} finally {$lock.Dispose()}
