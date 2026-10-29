#Requires -Version 7
[CmdletBinding()]
param([string]$Installation,[string]$ReceiptDirectory)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../../scripts/win/machine-env.ps1"
. "$PSScriptRoot/iis-runtime.ps1"
if(-not $Installation){$Installation=$env:LAPLACE_IIS_INSTALLATION}
if(-not $Installation){throw 'Declare LAPLACE_IIS_INSTALLATION or -Installation.'}
$c=Read-IisInstallation $Installation
$web=Join-Path $c.directory 'web'
$lock=$null
try{
    $lock=[IO.File]::Open((Join-Path $web '.publication.lock'),'Open','Read','None')
    $active=Get-Content -LiteralPath (Join-Path $web 'active.json') -Raw|ConvertFrom-Json
    if($active.revision -notmatch '^[a-f0-9]{7,64}$'){throw 'Invalid installed IIS release selection.'}
    $release=Join-Path $web ('releases/'+$active.revision)
    $manifest=Test-IisPackage $release
    if($manifest.revision -cne $active.revision){throw 'Installed release and package identities disagree.'}
    [xml]$configuration=Get-Content -LiteralPath (Join-Path $web 'web.config') -Raw
    $expected='.\releases\'+$active.revision+'\Laplace.Endpoints.OpenAICompat.exe'
    if($configuration.configuration.'system.webServer'.aspNetCore.processPath -cne $expected -or
       $configuration.configuration.'system.webServer'.aspNetCore.hostingModel -ne 'inprocess'){
        throw 'ANCM does not select the declared installed release.'
    }
    $result=Test-IisEndpoint $c -OperatorToken $env:LAPLACE_OPERATOR_TOKEN -ApiKey $env:LAPLACE_API_KEY
    $result.revision=$manifest.revision
    $result.release=$release
    $result.package_integrity='passed'
    $result.hosting='installed IIS / ANCM in-process'
    if(-not $ReceiptDirectory){$ReceiptDirectory=Join-Path $env:LAPLACE_WORK 'operations'}
    [void][IO.Directory]::CreateDirectory($ReceiptDirectory)
    $receipt=Join-Path $ReceiptDirectory ('iis-verification-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json')
    Write-IisAtomic $receipt (Iis-Json $result)
    $result.receipt=$receipt
    $result|ConvertTo-Json -Depth 6
}finally{if($lock){$lock.Dispose()}}
