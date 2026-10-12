#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installation,[Parameter(Mandatory)][string]$Package)
. "$PSScriptRoot/iis-runtime.ps1"
. "$PSScriptRoot/iis-access.ps1"
$c=Read-IisInstallation $Installation
if(-not $env:LAPLACE_API_KEY){throw 'Set LAPLACE_API_KEY to a workload key for authenticated publication verification.'}
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
            try{$result=Test-IisEndpoint $c -ApiKey $env:LAPLACE_API_KEY;break}catch{if($i -eq 11){throw};Start-Sleep -Seconds 2}
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
