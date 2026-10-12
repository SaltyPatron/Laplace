#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installation,[Parameter(Mandatory)][string]$Package)
. "$PSScriptRoot/iis-runtime.ps1"
$c=Read-IisInstallation $Installation
if(-not $env:LAPLACE_API_KEY){throw 'Set LAPLACE_API_KEY to a workload key for authenticated publication verification.'}
$manifest=Test-IisPackage $Package
if($manifest.revision -notmatch '^[a-f0-9]{7,64}$'){throw 'Invalid package revision.'}
$web=Join-Path $c.directory 'web';$target=Join-Path $web ('releases/'+$manifest.revision)
$lock=[IO.File]::Open((Join-Path $web '.publication.lock'),'OpenOrCreate','ReadWrite','None')
try {
    if(Test-Path $target){$null=Test-IisPackage $target}else{
        $null=New-Item -ItemType Directory -Path $target -Force
        Copy-Item -Path (Join-Path $Package '*') -Destination $target -Recurse
        $null=Test-IisPackage $target
    }
    $config=Join-Path $web 'web.config';$previous=Get-Content -LiteralPath $config -Raw
    try {
        Write-IisAtomic $config (Get-IisWebConfig $manifest.revision (Join-Path $c.directory 'state/logs'))
        for($i=0;$i -lt 12;$i++){
            try{$result=Test-IisEndpoint $c -ApiKey $env:LAPLACE_API_KEY;break}catch{if($i -eq 11){throw};Start-Sleep -Seconds 2}
        }
    } catch {
        Write-IisAtomic $config $previous
        throw
    }
    $result.revision=$manifest.revision
    $receipt=Join-Path $c.directory ('receipts/publication-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'.json')
    Write-IisAtomic $receipt (Iis-Json $result)
    Write-IisAtomic (Join-Path $web 'active.json') (Iis-Json @{revision=$manifest.revision})
    Write-Host "Published $($manifest.revision) at $($c.origin); authenticated SQL and native storage verified. Receipt: $receipt"
} finally {$lock.Dispose()}
