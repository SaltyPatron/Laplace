#Requires -Version 7
[CmdletBinding()]
param([string]$Installation,[string]$EngineBuild,[string]$ManagedBuild,[switch]$Publish,[switch]$ForceWeb,
    [ValidateSet('all','native','postgres','api','cli','uci','migrations','web')][string[]]$Components=@('all'),
    [switch]$Plan,[switch]$Force,[string]$Receipt,[string]$ComponentSelection)
$ErrorActionPreference='Stop'
if($ComponentSelection){$Components=$ComponentSelection.Split(',');foreach($component in $Components){if($component -notin @('all','native','postgres','api','cli','uci','migrations','web')){throw "Unknown component: $component"}}}
$requiresNative=@($Components | Where-Object {$_ -ne 'web'}).Count -gt 0
. "$PSScriptRoot/../../scripts/win/machine-env.ps1"
. "$PSScriptRoot/iis-runtime.ps1"
if(-not $Installation){$Installation=$env:LAPLACE_IIS_INSTALLATION}
if($Publish -and -not $Plan){
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
if($requiresNative -and -not($env:VSCMD_VER -and $env:SETVARS_COMPLETED -and (Get-Command icx.exe -ErrorAction SilentlyContinue))){throw 'The native build requires the configured MSVC and oneAPI developer environment.'}
. "$PSScriptRoot/../../scripts/win/laplace-paths.ps1"
Import-LaplaceEnv
if($EngineBuild){$env:LAPLACE_ENGINE_BUILD=[IO.Path]::GetFullPath($EngineBuild)}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$env:LAPLACE_ROOT=$repo
$cache=Join-Path $env:LAPLACE_ENGINE_BUILD 'CMakeCache.txt'
if($requiresNative -and (Test-Path -LiteralPath $cache)) {
    $homeLine=Select-String -LiteralPath $cache -Pattern '^CMAKE_HOME_DIRECTORY:INTERNAL=(.+)$'
    $configuredSource=if($homeLine){[IO.Path]::GetFullPath($homeLine.Matches[0].Groups[1].Value)}else{''}
    if($configuredSource.TrimEnd('\','/') -ine (Join-Path $repo 'engine').TrimEnd('\','/')) {
        throw "The native build directory belongs to another source tree: $configuredSource. Select this checkout's native build directory."
    }
}
$revision=(& git -C $repo rev-parse HEAD).Trim()
if($LASTEXITCODE){throw 'Cannot identify the source revision.'}
$selected=[Collections.Generic.HashSet[string]]::new([string[]]$Components)
if($selected.Contains('all') -or $selected.Contains('native') -or $selected.Contains('postgres')){
    foreach($name in 'native','postgres','api','cli','uci','migrations','web'){$null=$selected.Add($name)}
    $null=$selected.Remove('all')
}
if($selected.Contains('api')){$null=$selected.Add('web')}
if(-not $ManagedBuild){$ManagedBuild=$env:LAPLACE_BUILD_ROOT}
$ManagedBuild=[IO.Path]::GetFullPath($ManagedBuild)
$store=Join-Path $env:LAPLACE_WORK 'artifacts/windows-components'
$run=Join-Path $env:LAPLACE_WORK ('operations/components-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null=New-Item -ItemType Directory -Path $run -Force
$buildReceipt=Join-Path $run 'components.json'
$buildNames=@('native','api','cli','uci','migrations','web' | Where-Object {$selected.Contains($_)})
$buildOptions=@()
if($Plan){$buildOptions+='--plan'}
if($Force){$buildOptions+='--force'}
if($ForceWeb){$buildOptions+='--force-web'}
& python (Join-Path $repo 'scripts/windows-components.py') --root $repo --build $ManagedBuild --native $env:LAPLACE_ENGINE_BUILD --store $store --components ($buildNames -join ',') --output $buildReceipt @buildOptions
if($LASTEXITCODE){throw 'Component build failed; no application package was activated.'}
$buildResult=Get-Content -LiteralPath $buildReceipt -Raw | ConvertFrom-Json
if($Plan){
    Write-Host ('Selected components: '+(@($selected | Sort-Object) -join ', '))
    Write-Host ('Database migrations: '+$selected.Contains('migrations'))
    Write-Host ('PostgreSQL extension installation: '+$selected.Contains('postgres'))
    if($Receipt){Write-IisAtomic $Receipt (Iis-Json $buildResult)}
    return
}
if($Publish -and $selected.Contains('postgres')){
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
$published=Join-Path $run 'published'
$base=$null
if($Installation){
    if(-not (Get-Command Read-IisInstallation -ErrorAction SilentlyContinue)){. "$PSScriptRoot/iis-runtime.ps1"}
    $iisConfig=Read-IisInstallation $Installation
    $activePath=Join-Path $iisConfig.directory 'web/active.json'
    if(Test-Path -LiteralPath $activePath){
        $active=Get-Content -LiteralPath $activePath -Raw | ConvertFrom-Json
        $base=Join-Path $iisConfig.directory ('web/releases/'+$active.revision)
        $baseManifest=Test-IisPackage $base
    }
}
if(-not (Get-Command Copy-IisArtifactTree -ErrorAction SilentlyContinue)){. "$PSScriptRoot/iis-runtime.ps1"}
$complete=@('api','cli','uci','migrations','web' | Where-Object {-not $selected.Contains($_)}).Count -eq 0
$preserved=@()
if($base -and -not $complete){
    $preserved=@($baseManifest.files | Where-Object {
        $path=$_.path.Replace('\','/')
        $owner=if($path -match '^(cli|uci|migrations|wwwroot)/'){if($Matches[1] -eq 'wwwroot'){'web'}else{$Matches[1]}}else{'api'}
        -not $selected.Contains($owner)
    } | Sort-Object path)
}
$composition=[ordered]@{
    components=@($buildResult.components | Where-Object component -NE 'native' | ForEach-Object {
        [ordered]@{component=$_.component;identity=if($_.identity){$_.identity}else{$_.dist_sha256}}
    })
    preserved=$preserved
    packager=(Get-FileHash -LiteralPath "$PSScriptRoot/New-IisPackage.ps1").Hash
    runtime=(Get-FileHash -LiteralPath "$PSScriptRoot/iis-runtime.ps1").Hash
}
$compositionKey=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Iis-Json $composition)))).ToLowerInvariant()
$compositionReceipt=Join-Path $env:LAPLACE_WORK ('artifacts/iis-compositions/'+$compositionKey+'.json')
$package=$null
if(Test-Path -LiteralPath $compositionReceipt){
    $cached=Get-Content -LiteralPath $compositionReceipt -Raw | ConvertFrom-Json
    if(Test-Path -LiteralPath $cached.package){$null=Test-IisPackage $cached.package;$package=$cached.package;Write-Host 'Component composition unchanged: reused verified IIS package; no package files copied.'}
}
if(-not $package){
$null=New-Item -ItemType Directory -Path $published -Force
if($base -and -not $complete){Copy-IisArtifactTree $base $published @('iis-package.json')}
foreach($component in $buildResult.components){
    if($component.component -eq 'native'){continue}
    $part=switch($component.component){'api'{''};'web'{'wwwroot'};default{$component.component}}
    $destination=if($part){Join-Path $published $part}else{$published}
    if($part -and -not ([IO.Path]::GetFullPath($destination).StartsWith([IO.Path]::GetFullPath($published).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase))){throw 'Component destination escaped package assembly.'}
    if($part -and (Test-Path -LiteralPath $destination)){
        # Only this newly assembled package's links are removed.
        Remove-Item -LiteralPath $destination -Recurse -Force
    }elseif(-not $part){
        foreach($file in Get-ChildItem -LiteralPath $published -File -Recurse){
            $relative=[IO.Path]::GetRelativePath($published,$file.FullName).Replace('\','/')
            if($relative -notmatch '^(cli|uci|migrations|wwwroot)/'){Remove-Item -LiteralPath $file.FullName -Force}
        }
    }
    if($component.component -eq 'api'){Copy-IisArtifactTree $component.directory $destination @('wwwroot')}
    else{Copy-IisArtifactTree $component.directory $destination}
}
$files=@(Get-ChildItem -LiteralPath $published -File -Recurse | ForEach-Object {
    [IO.Path]::GetRelativePath($published,$_.FullName)+':'+(Get-FileHash -LiteralPath $_.FullName).Hash
} | Sort-Object)
$packageKey=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($files -join "`n")))).ToLowerInvariant()
$package=Join-Path $env:LAPLACE_WORK ('artifacts/iis-packages/'+$packageKey)
if(Test-Path -LiteralPath $package){$null=Test-IisPackage $package;Write-Host "Reused IIS package: $packageKey"}
else{& "$PSScriptRoot/New-IisPackage.ps1" -Published $published -Destination $package -Revision $revision}
# The assembled links are disposable; the content-addressed package owns its links.
if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($published)) -ne [IO.Path]::GetFullPath($run)){throw 'Invalid assembly cleanup path.'}
Remove-Item -LiteralPath $published -Recurse -Force
$null=New-Item -ItemType Directory -Path (Split-Path -Parent $compositionReceipt) -Force
Write-IisAtomic $compositionReceipt (Iis-Json @{package=$package;inputs=$composition})
}
if($Receipt){Write-IisAtomic $Receipt (Iis-Json @{components=$buildResult;package=$package;source_revision=$revision;selected=@($selected | Sort-Object)})}
if($Publish){
    # Use the staged release's migration runtime before selecting its IIS binary.
    # A failed migration must not activate an application requiring the new SQL.
    $migrationRuntime=Join-Path $package 'migrations/Laplace.Migrations.dll'
    foreach($database in @('application','substrate') | Where-Object {$selected.Contains('migrations')}){
        & pwsh -NoProfile -File "$PSScriptRoot/Invoke-IisDatabase.ps1" -Installation $Installation -MigrationRuntime $migrationRuntime -Database $database -Operation migrate
        if($LASTEXITCODE){throw "The $database migration failed; the staged IIS package was not activated."}
    }
    & "$PSScriptRoot/Publish-IisRuntime.ps1" -Installation $Installation -Package $package
    # Bind shared diagnostic files only after the new API's platform permission
    # checks are serving. A failed check is reported as an incomplete deployment.
    & pwsh -NoProfile -File "$PSScriptRoot/Invoke-IisDatabase.ps1" -Installation $Installation -MigrationRuntime $migrationRuntime -Database substrate -Operation status -ConfigureLogs
    if($LASTEXITCODE){throw 'IIS was published, but application log access verification failed. See the database operation receipt.'}
}
