#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installation,[string]$EngineBuild,[string]$ManagedBuild,[switch]$Publish)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../../scripts/win/machine-env.ps1"
if(-not($env:VSCMD_VER -and $env:SETVARS_COMPLETED)) {
    $developerShell = if($env:LAPLACE_DEVSHELL){$env:LAPLACE_DEVSHELL}else{Join-Path $env:LAPLACE_SRC 'Laplace-Operations/toolchain/windows/devshell.ps1'}
    if(-not(Test-Path -LiteralPath $developerShell -PathType Leaf)){throw "The configured developer shell is missing: $developerShell"}
    . $developerShell
}
if(-not($env:VSCMD_VER -and $env:SETVARS_COMPLETED -and (Get-Command icx.exe -ErrorAction SilentlyContinue))){throw 'The native build requires the configured MSVC and oneAPI developer environment.'}
. "$PSScriptRoot/../../scripts/win/laplace-paths.ps1"
Import-LaplaceEnv
. "$PSScriptRoot/iis-runtime.ps1"
$c=Read-IisInstallation $Installation
if($Publish -and -not $env:LAPLACE_API_KEY){throw 'Set LAPLACE_API_KEY for authenticated publication verification.'}
if($EngineBuild){$env:LAPLACE_ENGINE_BUILD=[IO.Path]::GetFullPath($EngineBuild)}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$env:LAPLACE_ROOT=$repo
$cache=Join-Path $env:LAPLACE_ENGINE_BUILD 'CMakeCache.txt'
if(Test-Path -LiteralPath $cache) {
    $homeLine=Select-String -LiteralPath $cache -Pattern '^CMAKE_HOME_DIRECTORY:INTERNAL=(.+)$'
    $configuredSource=if($homeLine){[IO.Path]::GetFullPath($homeLine.Matches[0].Groups[1].Value)}else{''}
    if($configuredSource.TrimEnd('\','/') -ine (Join-Path $repo 'engine').TrimEnd('\','/')) {
        throw "The native build directory belongs to another source tree: $configuredSource. Select this checkout's native build directory."
    }
}
$revision=(& git -C $repo rev-parse HEAD).Trim()
if($LASTEXITCODE){throw 'Cannot identify the source revision.'}
# Native SQL and managed callers belong to the same publication. The native
# builder owns configuration and the shared build-tree lock.
& (Join-Path $repo 'scripts/win/build-engine.cmd') laplace_core laplace_syzygy laplace_dynamics laplace_synthesis laplace_t0_perfcache laplace_highway_perfcache laplace_chess_position_perfcache
if($LASTEXITCODE){throw 'Native runtime build failed; no application package was created.'}
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
