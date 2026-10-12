#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installation,[string]$EngineBuild,[string]$ManagedBuild,[switch]$Publish)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../../scripts/win/machine-env.ps1"
. "$PSScriptRoot/../../scripts/win/laplace-paths.ps1"
Import-LaplaceEnv
. "$PSScriptRoot/iis-runtime.ps1"
$c=Read-IisInstallation $Installation
if($Publish -and -not $env:LAPLACE_API_KEY){throw 'Set LAPLACE_API_KEY for authenticated publication verification.'}
if($EngineBuild){$env:LAPLACE_ENGINE_BUILD=[IO.Path]::GetFullPath($EngineBuild)}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$revision=(& git -C $repo rev-parse HEAD).Trim()
if($LASTEXITCODE){throw 'Cannot identify the source revision.'}
$work=Join-Path $env:LAPLACE_WORK ('iis-packages/'+$revision+'-'+[Guid]::NewGuid().ToString('N'))
$published=Join-Path $work 'published';$package=Join-Path $work 'package'
$null=New-Item -ItemType Directory -Path $published -Force
if(-not $ManagedBuild){$ManagedBuild=Join-Path $env:LAPLACE_BUILD_ROOT 'iis-publish'}
$ManagedBuild=[IO.Path]::GetFullPath($ManagedBuild)
foreach($component in @(@('Laplace.Endpoints.OpenAICompat',$published),@('Laplace.Cli',(Join-Path $published 'cli')),@('Laplace.Chess.Uci',(Join-Path $published 'uci')))){
    & dotnet publish (Join-Path $repo ('app/'+$component[0]+'/'+$component[0]+'.csproj')) -c Release --no-self-contained -o $component[1] --nologo "-p:LAPLACE_BUILD_ROOT=$ManagedBuild" "-p:LAPLACE_ENGINE_BUILD=$env:LAPLACE_ENGINE_BUILD"
    if($LASTEXITCODE){throw "Publish failed: $($component[0])"}
}
Push-Location (Join-Path $repo 'web')
try {
    if (-not (Test-Path -LiteralPath 'openapi/openapi.json')) { throw 'The endpoint build did not generate web/openapi/openapi.json.' }
    & npm ci --no-audit --no-fund
    if($LASTEXITCODE){throw 'Frontend dependency installation failed.'}
    & npm run build
    if($LASTEXITCODE){throw 'Frontend build failed.'}
    $null=New-Item -ItemType Directory -Path (Join-Path $published 'wwwroot') -Force
    Copy-Item -Path 'dist/*' -Destination (Join-Path $published 'wwwroot') -Recurse -Force
}finally{Pop-Location}
& "$PSScriptRoot/New-IisPackage.ps1" -Published $published -Destination $package -Revision $revision
if($Publish){& "$PSScriptRoot/Publish-IisRuntime.ps1" -Installation $Installation -Package $package}
