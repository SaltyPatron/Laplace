#Requires -Version 7
[CmdletBinding()]
param([string]$Installation,[string]$EngineBuild,[string]$ManagedBuild,[switch]$Publish)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../../scripts/win/machine-env.ps1"
if(-not $Installation){$Installation=$env:LAPLACE_IIS_INSTALLATION}
if($Publish){
    if(-not $Installation){throw 'Publication requires an IIS installation. Use -Installation or LAPLACE_IIS_INSTALLATION.'}
    $hasDevIdentity = $false
    if(-not $env:LAPLACE_API_KEY -and -not $env:LAPLACE_OPERATOR_TOKEN){
        $devInstallation=if($env:LAPLACE_DEVELOPMENT_INSTALLATION){$env:LAPLACE_DEVELOPMENT_INSTALLATION}elseif($env:LAPLACE_SRC){Join-Path $env:LAPLACE_SRC 'Laplace-Operations/development/installation.json'}else{''}
        $credHelper=if($devInstallation){Join-Path (Split-Path -Parent $devInstallation) 'credentials.ps1'}else{''}
        if($devInstallation -and (Test-Path -LiteralPath $devInstallation -PathType Leaf) -and (Test-Path -LiteralPath $credHelper -PathType Leaf)){
            try {
                . $credHelper
                $devConfig=Read-DevelopmentConfig $devInstallation
                $credPath=Get-DevelopmentCredentialPath $devConfig
                if($credPath -and (Test-Path -LiteralPath $credPath -PathType Leaf)){$hasDevIdentity=$true}
            } catch { Write-Warning ('Development identity preflight failed: '+$_.Exception.GetType().Name+'. Check the declared enrollment and configuration.') }
        }
    }
    if(-not $env:LAPLACE_API_KEY -and -not $env:LAPLACE_OPERATOR_TOKEN -and -not $hasDevIdentity){throw 'Set LAPLACE_API_KEY for authenticated publication verification.'}
    . "$PSScriptRoot/iis-runtime.ps1"
    $null=Read-IisInstallation $Installation
}
if(-not($env:VSCMD_VER -and $env:SETVARS_COMPLETED)) {
    $developerShell = if($env:LAPLACE_DEVSHELL){$env:LAPLACE_DEVSHELL}else{Join-Path $env:LAPLACE_SRC 'Laplace-Operations/toolchain/windows/devshell.ps1'}
    if(-not(Test-Path -LiteralPath $developerShell -PathType Leaf)){throw "The configured developer shell is missing: $developerShell"}
    . $developerShell
}
if(-not($env:VSCMD_VER -and $env:SETVARS_COMPLETED -and (Get-Command icx.exe -ErrorAction SilentlyContinue))){throw 'The native build requires the configured MSVC and oneAPI developer environment.'}
. "$PSScriptRoot/../../scripts/win/laplace-paths.ps1"
Import-LaplaceEnv
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
& (Join-Path $repo 'scripts/win/build-engine.cmd') laplace_core laplace_syzygy laplace_dynamics laplace_synthesis laplace_t0_perfcache laplace_highway_perfcache laplace_chess_position_perfcache laplace_chess_transition_perfcache
if($LASTEXITCODE){throw 'Native runtime build failed; no application package was created.'}
if($Publish){
    $iisConfig=Read-IisInstallation $Installation
    $accessPath=if($iisConfig.database_access_file){$iisConfig.database_access_file}else{Join-Path $env:LAPLACE_SRC 'Laplace-Operations/development/installation.json'}
    if(-not(Test-Path -LiteralPath $accessPath -PathType Leaf)){throw "The declared local database access file is missing: $accessPath"}
    $access=Get-Content -LiteralPath $accessPath -Raw | ConvertFrom-Json
    if($access.authentication -ne 'sspi' -or $access.database_host -ine $env:COMPUTERNAME -or
        $iisConfig.database.host -ine $access.database_host -or $iisConfig.database.port -ne $access.port -or
        -not $access.admin_role -or -not(Test-Path -LiteralPath $access.root_certificate -PathType Leaf)){
        throw 'Extension publication requires the declared local IIS database, Windows SSPI role and TLS root certificate.'
    }
    # Match database maintenance: inherited PGHOSTADDR/PGSERVICE/PGOPTIONS or
    # password settings must not redirect the declared SSPI installation.
    $savedPg=@{}
    $pgNames=@(Get-ChildItem Env: | Where-Object {$_.Name -like 'PG*' -and $_.Name -ne 'PGBIN'} | ForEach-Object Name)+@(
        'LAPLACE_PGHOST','LAPLACE_PGPORT','LAPLACE_PGUSER','PGSSLMODE','PGSSLROOTCERT','PGREQUIREAUTH','PGCONNECT_TIMEOUT')
    foreach($name in $pgNames | Select-Object -Unique){
        $savedPg[$name]=[Environment]::GetEnvironmentVariable($name)
        if($name -notlike 'LAPLACE_*'){Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue}
    }
    try {
        $env:LAPLACE_PGHOST=$access.database_host
        $env:LAPLACE_PGPORT=[string]$access.port
        $env:LAPLACE_PGUSER=$access.admin_role
        $env:PGSSLMODE='verify-full';$env:PGSSLROOTCERT=$access.root_certificate
        $env:PGREQUIREAUTH='sspi';$env:PGCONNECT_TIMEOUT='10'
        & (Join-Path $repo 'scripts/win/install-extensions.cmd') --check-connection
        if($LASTEXITCODE){throw 'PostgreSQL extension connection preflight failed; installed extension files were not changed.'}
        & (Join-Path $repo 'scripts/win/build-extensions.cmd')
        if($LASTEXITCODE){throw 'PostgreSQL extension build failed; no application package was created.'}
        & (Join-Path $repo 'scripts/win/install-extensions.cmd') --skip-build --recycle
        if($LASTEXITCODE){throw 'PostgreSQL extension installation failed; no application package was created.'}
    } finally {
        foreach($name in $savedPg.Keys){[Environment]::SetEnvironmentVariable($name,$savedPg[$name])}
    }
}
$work=Join-Path $env:LAPLACE_WORK ('iis-packages/'+$revision+'-'+[Guid]::NewGuid().ToString('N'))
$published=Join-Path $work 'published';$package=Join-Path $work 'package'
$null=New-Item -ItemType Directory -Path $published -Force
$previousLogDirectory=$env:LAPLACE_OPS_LOG_DIR
try {
$env:LAPLACE_OPS_LOG_DIR=Join-Path $work 'logs'
if(-not $ManagedBuild){$ManagedBuild=Join-Path $env:LAPLACE_BUILD_ROOT 'iis-publish'}
$ManagedBuild=[IO.Path]::GetFullPath($ManagedBuild)
foreach($component in @(@('Laplace.Endpoints.OpenAICompat',$published),@('Laplace.Cli',(Join-Path $published 'cli')),@('Laplace.Chess.Uci',(Join-Path $published 'uci')),@('Laplace.Migrations',(Join-Path $published 'migrations')))){
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
if($Publish){
    # Use the staged release's migration runtime before selecting its IIS binary.
    # A failed migration must not activate an application requiring the new SQL.
    $migrationRuntime=Join-Path $package 'migrations/Laplace.Migrations.dll'
    foreach($database in @('application','substrate')){
        & pwsh -NoProfile -File "$PSScriptRoot/Invoke-IisDatabase.ps1" -Installation $Installation -MigrationRuntime $migrationRuntime -Database $database -Operation migrate
        if($LASTEXITCODE){throw "The $database migration failed; the staged IIS package was not activated."}
    }
    & "$PSScriptRoot/Publish-IisRuntime.ps1" -Installation $Installation -Package $package
    # Bind shared diagnostic files only after the new API's platform permission
    # checks are serving. A failed check is reported as an incomplete deployment.
    & pwsh -NoProfile -File "$PSScriptRoot/Invoke-IisDatabase.ps1" -Installation $Installation -MigrationRuntime $migrationRuntime -Database substrate -Operation status -ConfigureLogs
    if($LASTEXITCODE){throw 'IIS was published, but application log access verification failed. See the database operation receipt.'}
}
}finally{[Environment]::SetEnvironmentVariable('LAPLACE_OPS_LOG_DIR',$previousLogDirectory)}
