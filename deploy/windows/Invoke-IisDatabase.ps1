#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('substrate','application')][string]$Database,
    [ValidateSet('status','migrate','repair','remigrate','recreate')][string]$Operation='status',
    [string]$Installation,
    [string]$AccessConfiguration,
    [string]$MigrationRuntime,
    [string]$ReceiptDirectory,
    [switch]$InstalledCredentials,
    [switch]$Yes
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../../scripts/win/machine-env.ps1"
. "$PSScriptRoot/iis-runtime.ps1"
. "$PSScriptRoot/iis-owner-lifecycle.ps1"
if(-not $Installation){$Installation=$env:LAPLACE_IIS_INSTALLATION}
if(-not $Installation){throw 'Declare the IIS installation with LAPLACE_IIS_INSTALLATION or -Installation.'}
$c=Read-IisInstallation $Installation
if(-not $AccessConfiguration){$AccessConfiguration=if($c.database_access_file){$c.database_access_file}else{Join-Path $env:LAPLACE_SRC 'Laplace-Operations/development/installation.json'}}
$access=Get-Content -LiteralPath $AccessConfiguration -Raw | ConvertFrom-Json
if($access.authentication -ne 'sspi' -or (Get-IisDatabaseAuthentication $c) -ne 'sspi' -or
    $access.database_host -ine $env:COMPUTERNAME -or $c.database.host -ine $access.database_host -or
    $c.database.port -ne $access.port){throw 'Select the declared local IIS installation and Windows SSPI database access.'}
foreach($name in 'substrate_database','application_database','substrate_role','application_role'){
    if($c.database.$name -cne $access.$name){throw "IIS and access declarations disagree on $name."}
}
foreach($value in $access.substrate_database,$access.application_database,$access.substrate_role,$access.application_role,$access.application_owner,$access.admin_role){
    if($value -notmatch '^[A-Za-z0-9_-]+$'){throw 'Invalid declared database or role name.'}
}
if($access.substrate_database -ceq $access.application_database){throw 'Select distinct application and substrate databases.'}
$selected=if($Database -eq 'application'){$access.application_database}else{$access.substrate_database}
$role=if($Database -eq 'application'){$access.application_role}else{$access.substrate_role}
if($selected -in 'postgres','template0','template1'){throw 'A system database cannot be maintained through this command.'}
$usesInstalledRuntime=-not $MigrationRuntime
if($usesInstalledRuntime){
    $web=Join-Path $c.directory 'web'
    $config=Join-Path $web 'web.config'
    [xml]$xml=Get-Content -LiteralPath $config -Raw
    $release=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath((Join-Path $web $xml.configuration.'system.webServer'.aspNetCore.processPath)))
    $releases=[IO.Path]::GetFullPath((Join-Path $web 'releases')).TrimEnd('\')+'\'
    if(-not $release.StartsWith($releases,[StringComparison]::OrdinalIgnoreCase)){throw 'The IIS executable is outside its declared releases directory.'}
    $MigrationRuntime=Join-Path $release 'migrations/Laplace.Migrations.dll'
}
$MigrationRuntime=[IO.Path]::GetFullPath($MigrationRuntime)
if(-not(Test-Path -LiteralPath $MigrationRuntime -PathType Leaf)){throw "Installed migrations are missing: $MigrationRuntime. Publish a package containing migrations or explicitly supply -MigrationRuntime from the matching release."}
$psql=$access.psql
if(-not(Test-Path -LiteralPath $psql -PathType Leaf)){throw 'The declared PostgreSQL client is missing.'}
if(-not(Test-Path -LiteralPath $access.root_certificate -PathType Leaf)){throw 'The declared PostgreSQL root certificate is missing.'}
$null=Get-Command dotnet -ErrorAction Stop
Write-Host "$Operation $Database database $selected on $($access.database_host):$($access.port) as $($access.admin_role)"
if($Operation -in 'remigrate','recreate' -and -not $Yes){
    $effect=if($Operation -eq 'recreate'){'delete its data and recreate it'}else{'clear its migration journal and reapply migrations'}
    if((Read-Host "Type $selected to $effect") -cne $selected){throw 'Operation canceled before database changes.'}
}
if(-not $ReceiptDirectory){$ReceiptDirectory=Join-Path $env:LAPLACE_WORK 'operations'}
$receiptRoot=Join-Path $ReceiptDirectory ('iis-database-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null=New-Item -ItemType Directory -Path $receiptRoot
$saved=@{}
$identityNames=@('LAPLACE_PLATFORM_OWNER_POLICY','LAPLACE_PLATFORM_OWNER_POLICY_FILE','LAPLACE_AUTH_GOOGLE_CLIENT_ID','LAPLACE_AUTH_MICROSOFT_CLIENT_ID')
foreach($name in @('LAPLACE_DB','LAPLACE_APP_DB','LAPLACE_OPS_LOG_DIR','DATABASE_URL')+$identityNames+@(Get-ChildItem Env: | Where-Object Name -Like 'PG*' | ForEach-Object Name)){
    $saved[$name]=[Environment]::GetEnvironmentVariable($name)
    Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue
}
$receipt=[ordered]@{host=$env:COMPUTERNAME;database=$selected;scope=$Database;operation=$Operation;runtime=$MigrationRuntime;runtime_selection=$(if($usesInstalledRuntime){'installed'}else{'explicit'});runtime_sha256=(Get-FileHash -LiteralPath $MigrationRuntime -Algorithm SHA256).Hash;started_utc=[DateTime]::UtcNow.ToString('o');completed=$false}
function Query([string]$Sql,[string]$Name='postgres'){
    $result=$Sql | & $psql -X -w -h $access.database_host -p $access.port -U $access.admin_role -d $Name -Atq -v ON_ERROR_STOP=1 -f - 2>&1
    if($LASTEXITCODE){throw ($result -join "`n")}
    ($result -join "`n").Trim()
}
function Migrate([string]$Verb){
    & dotnet $MigrationRuntime $Verb --yes
    if($LASTEXITCODE){throw "Migration command $Verb failed (exit $LASTEXITCODE)."}
}
function Connection([string]$Name){
    $builder=[System.Data.Common.DbConnectionStringBuilder]::new()
    $builder['Host']=$access.database_host;$builder['Port']=[string]$access.port
    $builder['Username']=$access.admin_role;$builder['Database']=$Name
    $builder['SSL Mode']='VerifyFull';$builder['Root Certificate']=$access.root_certificate
    # Npgsql 10 parses this case-sensitively; libpq's PGREQUIREAUTH uses lowercase.
    $builder['Require Auth']='SSPI'
    $builder.ConnectionString
}
$lock=$null
try {
    $env:PGSSLMODE='verify-full';$env:PGSSLROOTCERT=$access.root_certificate
    $env:PGREQUIREAUTH='sspi';$env:PGCONNECT_TIMEOUT='10';$env:PGPASSFILE=Join-Path $receiptRoot 'unused-no-password-file'
    $env:LAPLACE_DB=Connection $access.substrate_database
    $env:LAPLACE_APP_DB=Connection $access.application_database
    $env:LAPLACE_OPS_LOG_DIR=Join-Path $receiptRoot 'logs'
    # Even --help initializes persistent logging. Configure its mutable output
    # before starting the staged runtime; the package manifest must remain sealed.
    $savedNoColor=$env:NO_COLOR
    try {
        $env:NO_COLOR='1'
        $help=& dotnet $MigrationRuntime --help 2>&1
    }finally{
        [Environment]::SetEnvironmentVariable('NO_COLOR',$savedNoColor)
    }
    if($LASTEXITCODE){throw "The selected migration runtime cannot start (exit $LASTEXITCODE): $($help -join "`n")"}
    $needed=if($Database -eq 'application'){@('app-status','app-up','app-check','app-reset','app-nuke')}else{@('substrate-status','substrate-up','reset','nuke')}
    foreach($verb in $needed){
        if(($help -join "`n") -notmatch ('(?m)^\s*'+[regex]::Escape($verb)+'(?:\s|$)')){throw "The selected migration runtime does not support $verb. Publish the matching migration runtime before maintenance."}
    }
    if($Database -eq 'application'){
        $identityRecord=$null
        $receipt.platform_owner_policy='no-owner-config'
        if($InstalledCredentials){
            $identityRecord=Read-IisWorkloadIdentity $c (Join-Path $c.directory 'agent') $null
        }elseif($access.PSObject.Properties['identity_reference']){
            if(($access.identity_reference -replace '^kv/','kv/data/') -cne $c.records.identity -or
                $access.client -ine $c.host -or $access.bao_address.TrimEnd('/') -cne $c.address.TrimEnd('/')){
                throw 'The development identity record differs from the IIS installation.'
            }
            $credentialHelper=Join-Path (Split-Path -Parent $AccessConfiguration) 'credentials.ps1'
            if(-not(Test-Path -LiteralPath $credentialHelper -PathType Leaf)){throw 'The declared development credential helper is missing.'}
            . $credentialHelper
            $token=$null
            try {
                $token=New-DevelopmentBaoSession $access
                $identityRecord=(Invoke-DevelopmentBao $access GET $c.records.identity $token).data.data
            }finally{
                try{if($token){$null=Invoke-DevelopmentBao $access POST 'auth/token/revoke-self' $token @{}}}
                finally{$token=$null}
            }
        }
        if($identityRecord){
            if($c.platform_owner_declaration_file -and $Operation -ne 'status'){
                $ownerDeclaration=Read-IisOwnerJson $c.platform_owner_declaration_file
                $ownerEnrollment=Sync-IisPlatformOwnerPolicy $c $identityRecord $ownerDeclaration {Read-IisOwnerBindings $c $access}
                $receipt.owner_count=$ownerEnrollment.owner_count;$receipt.owner_policy_changed=$ownerEnrollment.changed
            }
            $identityValues=Get-IisInstallationIdentityEnvironment $c $identityRecord
            foreach($name in $identityValues.Keys){[Environment]::SetEnvironmentVariable($name,$identityValues[$name])}
            if($identityValues.Contains('LAPLACE_PLATFORM_OWNER_POLICY')){$receipt.platform_owner_policy='configured'}
            $identityRecord=$null;$identityValues=$null
        }elseif($c.platform_owner_policy_file -or $c.platform_owner_declaration_file){throw 'The declared owner policy requires installation identity configuration before database maintenance.'}
        Write-Host "Platform owner policy: $($receipt.platform_owner_policy)"
    }
    # Only installed-runtime selection depends on the active IIS publication.
    if($usesInstalledRuntime){
        $lock=[IO.File]::Open((Join-Path $web '.publication.lock'),'OpenOrCreate','ReadWrite','None')
        [xml]$current=Get-Content -LiteralPath $config -Raw
        if($current.configuration.'system.webServer'.aspNetCore.processPath -cne $xml.configuration.'system.webServer'.aspNetCore.processPath){throw 'The installed release changed; retry against the active package.'}
    }
    $receipt.cluster=Query 'SELECT system_identifier FROM pg_control_system();'
    $actualData=Query 'SHOW data_directory;'
    if([IO.Path]::GetFullPath($actualData) -ine [IO.Path]::GetFullPath($env:LAPLACE_PGDATA)){throw 'The connection reaches another PostgreSQL data directory.'}
    if($Operation -ne 'status'){
        $requiredRoles=@($role,$access.admin_role)
        if($Database -eq 'application'){$requiredRoles+=@($access.application_owner)}
        foreach($requiredRole in $requiredRoles){
            if((Query "SELECT count(*) FROM pg_roles WHERE rolname='$requiredRole';") -ne '1'){throw "Declared role $requiredRole is missing; configure the installation roles before database maintenance."}
        }
        if($Database -eq 'substrate'){
            foreach($extension in 'postgis','laplace_geom','laplace_substrate','file_fdw','pg_stat_statements'){
                if((Query "SELECT count(*) FROM pg_available_extensions WHERE name='$extension';") -ne '1'){throw "Required substrate extension $extension is unavailable in this PostgreSQL installation."}
            }
        }
    }
    $prefix=if($Database -eq 'application'){'app-'}else{'substrate-'}
    switch($Operation){
        status { Migrate ($prefix+'status') }
        {$_ -in 'migrate','repair'} { Migrate ($prefix+'up') }
        remigrate {
            Migrate $(if($Database -eq 'application'){'app-reset'}else{'reset'})
            Migrate ($prefix+'up')
        }
        recreate { Migrate $(if($Database -eq 'application'){'app-nuke'}else{'nuke'}) }
    }
    if($Operation -ne 'status' -and $Database -eq 'substrate'){
        $deployExtDir=Join-Path $env:LAPLACE_DATA_ROOT 'deploy/share/extension'
        if(-not(Test-Path -LiteralPath $deployExtDir -PathType Container)){
            $deployExtDir=Join-Path $env:LAPLACE_DATA_ROOT 'deploy/pgsql-18/share/extension'
        }
        foreach($ext in 'laplace_geom','laplace_substrate'){
            $installed=(Query "SELECT extversion FROM pg_extension WHERE extname='$ext';" $selected)
            $avail=(Query "SELECT default_version FROM pg_available_extensions WHERE name='$ext';" $selected)
            if($installed -and $avail -and $installed -cne $avail){
                $upgradeSrc=Join-Path $deployExtDir "${ext}_upgrade.sql"
                if(Test-Path -LiteralPath $upgradeSrc -PathType Leaf){
                    Copy-Item -LiteralPath $upgradeSrc -Destination (Join-Path $deployExtDir "${ext}--${installed}--${avail}.sql") -Force
                }
                Write-Host "Upgrading extension $ext from $installed to $avail in $selected"
                $null=Query "SET lock_timeout = '20s'; ALTER EXTENSION $ext UPDATE TO '$avail';" $selected
            }
        }
    }
    if($Operation -ne 'status'){
        # Recreating an absent database cannot copy its old grants; the access declaration owns them.
        $null=Query ('REVOKE CONNECT ON DATABASE "'+$selected+'" FROM PUBLIC; GRANT CONNECT ON DATABASE "'+$selected+'" TO "'+$role+'";')
        if($Database -eq 'application'){
            $owner=$access.application_owner
            $null=Query ('ALTER DATABASE "'+$selected+'" OWNER TO "'+$owner+'";')
            $null=Query ('ALTER SCHEMA app OWNER TO "'+$owner+'"; REVOKE CREATE ON SCHEMA public FROM PUBLIC;') $selected
            $schemas=@('app')
            $owners=@($owner,$access.admin_role)
        }else{
            $null=Query "GRANT pg_read_all_stats, pg_signal_backend TO `"$role`";"
            $schemas=(Query "SELECT nspname FROM pg_namespace WHERE nspname !~ '^pg_' AND nspname NOT IN ('information_schema','app') ORDER BY nspname;" $selected) -split "`n"
            $owners=@($access.admin_role)
        }
        foreach($schema in $schemas){
            if($schema -notmatch '^[A-Za-z0-9_]+$'){throw 'Unexpected schema identifier.'}
            $permissions=if($schema -eq 'public'){'SELECT'}elseif($Database -eq 'substrate'){'SELECT,INSERT,UPDATE,DELETE,MAINTAIN'}else{'SELECT,INSERT,UPDATE,DELETE'}
            $null=Query "GRANT USAGE ON SCHEMA `"$schema`" TO `"$role`"; GRANT $permissions ON ALL TABLES IN SCHEMA `"$schema`" TO `"$role`"; GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA `"$schema`" TO `"$role`";" $selected
            if($Database -eq 'substrate'){$null=Query "GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA `"$schema`" TO `"$role`";" $selected}
            foreach($owner in $owners){$null=Query "ALTER DEFAULT PRIVILEGES FOR ROLE `"$owner`" IN SCHEMA `"$schema`" GRANT $permissions ON TABLES TO `"$role`"; ALTER DEFAULT PRIVILEGES FOR ROLE `"$owner`" IN SCHEMA `"$schema`" GRANT USAGE,SELECT ON SEQUENCES TO `"$role`";" $selected}
        }
        if($Database -eq 'application'){
            Migrate 'app-check'
            $null=Query "SET ROLE `"$role`"; SELECT 1 FROM app.billing_quotes LIMIT 1; SELECT 1 FROM app.users LIMIT 1; RESET ROLE;" $selected
        }else{
            Migrate 'substrate-status'
            $null=Query "SET ROLE `"$role`"; SELECT EXISTS (SELECT FROM laplace.entities), EXISTS (SELECT FROM laplace.consensus); SELECT * FROM ops.substrate_counts(); SELECT laplace.word_id('the'); RESET ROLE;" $selected
            $receipt.substrate_runtime_access_verified=$true
        }
    }
    $receipt.completed=$true
}catch{
    $receipt.error=$_.Exception.Message
    throw
}finally{
    if($lock){$lock.Dispose()}
    $receipt.finished_utc=[DateTime]::UtcNow.ToString('o')
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $receiptRoot 'receipt.json') -Encoding utf8
    foreach($name in @('LAPLACE_DB','LAPLACE_APP_DB','LAPLACE_OPS_LOG_DIR','DATABASE_URL')+$identityNames+@(Get-ChildItem Env: | Where-Object Name -Like 'PG*' | ForEach-Object Name)){Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue}
    foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
    Write-Host "Receipt: $(Join-Path $receiptRoot 'receipt.json')"
}
