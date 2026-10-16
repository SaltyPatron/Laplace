#Requires -Version 7
[CmdletBinding()]
param([string]$Installation,[Parameter(Mandatory)][string]$Package)
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
$operatorToken=if($env:LAPLACE_OPERATOR_TOKEN){$env:LAPLACE_OPERATOR_TOKEN}else{''}
$apiKey=if($env:LAPLACE_API_KEY){$env:LAPLACE_API_KEY}else{''}
if(-not $apiKey -and -not $operatorToken){
    $devInstallation=if($env:LAPLACE_DEVELOPMENT_INSTALLATION){$env:LAPLACE_DEVELOPMENT_INSTALLATION}else{Join-Path $env:LAPLACE_SRC 'Laplace-Operations/development/installation.json'}
    $credHelper=Join-Path (Split-Path -Parent $devInstallation) 'credentials.ps1'
    if((Test-Path -LiteralPath $devInstallation -PathType Leaf) -and (Test-Path -LiteralPath $credHelper -PathType Leaf)){
        . $credHelper
        $devConfig=Get-Content -LiteralPath $devInstallation -Raw | ConvertFrom-Json
        $token=New-DevelopmentBaoSession $devConfig
        try {
            $path=(Get-DevelopmentSecretPaths $devConfig)['laplace-api'] -replace '^kv/', 'kv/data/'
            $operatorToken=(Invoke-DevelopmentBao $devConfig GET $path $token).data.data.operator_token
        } finally {
            try { if($token){ $null=Invoke-DevelopmentBao $devConfig POST 'auth/token/revoke-self' $token @{} } } catch {}
        }
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
        Grant-IisRuntimeReaders $target $readers
    }else{
        $null=New-Item -ItemType Directory -Path $target -Force
        Grant-IisRuntimeReaders $target $readers
        Copy-Item -Path (Join-Path $Package '*') -Destination $target -Recurse
        $null=Test-IisPackage $target
    }
    $config=Join-Path $web 'web.config';$previous=Get-Content -LiteralPath $config -Raw
    try {
        Write-IisPublicAtomic $config (Get-IisWebConfig $manifest.revision (Join-Path $c.directory 'state/logs')) $readers
        for($i=0;$i -lt 12;$i++){
            try{$result=Test-IisEndpoint $c -OperatorToken $operatorToken -ApiKey $apiKey;break}catch{if($i -eq 11){throw};Start-Sleep -Seconds 2}
        }
    } catch {
        Write-IisPublicAtomic $config $previous $readers
        throw
    }
    $result.revision=$manifest.revision
    $receipt=Join-Path $c.directory ('receipts/publication-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'.json')
    Write-IisAtomic $receipt (Iis-Json $result)
    Write-IisPublicAtomic (Join-Path $web 'active.json') (Iis-Json @{revision=$manifest.revision}) $readers
    Write-Host "Published $($manifest.revision) at $($c.origin); authenticated SQL and native storage verified. Receipt: $receipt"
} finally {$lock.Dispose()}
