#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installation,[Parameter(Mandatory)][string]$Published)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../../scripts/win/machine-env.ps1"
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$revision=(& git -C $repo rev-parse HEAD).Trim()
if($LASTEXITCODE){throw 'Cannot identify the source revision.'}
$destination=Join-Path $env:LAPLACE_WORK ('iis-packages/'+$revision+'-'+[Guid]::NewGuid().ToString('N'))
& "$PSScriptRoot/New-IisPackage.ps1" -Published $Published -Destination $destination -Revision $revision
& "$PSScriptRoot/Publish-IisRuntime.ps1" -Installation $Installation -Package $destination
