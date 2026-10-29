#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Work)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/../deploy/windows/database-connection.ps1"
. "$PSScriptRoot/../deploy/windows/iis-runtime.ps1"
$root=Join-Path ([IO.Path]::GetFullPath($Work)) ('database-connection-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$trust=Join-Path $root 'postgres-root.crt'
[IO.File]::WriteAllText($trust,'connection-construction-fixture')
$selection=@{host=$env:COMPUTERNAME;port=5432;database='laplace-mono';role='laplace_desktop_db';pool_max=4;authentication='sspi';root_certificate=$trust}
function Parsed([string]$Value){$b=[Data.Common.DbConnectionStringBuilder]::new();$b.set_ConnectionString($Value);return ,$b}
function Denied([scriptblock]$Action,[string]$Expected){
    try{& $Action}catch{if($_.Exception.Message -notmatch $Expected){throw};return}
    throw 'An invalid database selection was accepted.'
}
$local=Parsed (New-LaplaceWindowsDatabaseConnection $selection)
if($local.ContainsKey('Password') -or $local['Host'] -ne $env:COMPUTERNAME -or $local['SSL Mode'] -ne 'VerifyFull'){throw 'Local Windows identity or TLS contract changed.'}
$policy=(Resolve-Path "$PSScriptRoot/../deploy/windows/database-connection.ps1").Path.Replace("'","''")
$selectionJson=($selection|ConvertTo-Json -Compress).Replace("'","''")
$program=". '$policy'; New-LaplaceWindowsDatabaseConnection (ConvertFrom-Json -AsHashtable '$selectionJson')"
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($program))
$renderings=@(1..4|ForEach-Object {
    $value=& pwsh -NoProfile -EncodedCommand $encoded
    if($LASTEXITCODE){throw 'Fresh-process connection rendering failed.'}
    $value
}|Select-Object -Unique)
if($renderings.Count -ne 1){throw 'Equivalent connection settings changed bytes across credential-rendering processes.'}
$selection.host='hart-server'
Denied {New-LaplaceWindowsDatabaseConnection $selection} 'requires this machine'
$selection.host=$env:COMPUTERNAME
$record=[pscustomobject]@{database_host=$env:COMPUTERNAME;port=5432;database='laplace-mono';role='laplace_desktop_db';password=('a'*64)}
Denied {New-LaplaceWindowsDatabaseConnection $selection $record} 'must not consume'
$selection.authentication='scram';$selection.host='hart-server';$record.database_host='hart-server'
$remote=Parsed (New-LaplaceWindowsDatabaseConnection $selection $record)
if($remote['Password'] -ne $record.password -or $remote['SSL Mode'] -ne 'VerifyFull'){throw 'Remote credential or TLS policy changed.'}
$record.database_host='hart-prod'
Denied {New-LaplaceWindowsDatabaseConnection $selection $record} 'differs from this installation'
$selection.authentication='trust'
Denied {New-LaplaceWindowsDatabaseConnection $selection} 'Unsupported'
$selection.authentication='sspi';$selection.host=$env:COMPUTERNAME;$selection.root_certificate=Join-Path $root 'missing.crt'
Denied {New-LaplaceWindowsDatabaseConnection $selection} 'existing absolute file'
$null=New-Item -ItemType Directory -Path (Join-Path $root 'state')
Copy-Item -LiteralPath $trust -Destination (Join-Path $root 'state/postgres-root.crt')
$iis=[pscustomobject]@{
    directory=$root;origin='https://dev.example.test';environment=[pscustomobject]@{LAPLACE_DATA=$root}
    chess=[pscustomobject]@{match_engines=$null}
    database=[pscustomobject]@{authentication='sspi';host=$env:COMPUTERNAME;port=5432;pool_max=4;substrate_database='laplace-mono';substrate_role='laplace_desktop_db';application_database='laplace-desktop-app';application_role='laplace_desktop_app'}
    records=[pscustomobject]@{api='kv/data/fixture/api';identity='kv/data/fixture/identity';certificate='kv/data/fixture/certificate';chess='kv/data/fixture/chess'}
}
$records=[pscustomobject]@{
    api=[pscustomobject]@{operator_token=('b'*64)}
    identity=[pscustomobject]@{google_client_id='fixture';google_client_secret='fixture';microsoft_client_id='fixture';microsoft_client_secret='fixture'}
}
$references=@(Get-IisCredentialReferences $iis)
if($references.Count -ne 4 -or @($references | Where-Object Name -In @('substrate','appdb')).Count){throw 'IIS SSPI still requires database password records.'}
$environment=Get-IisEnvironment $iis $records
if($environment.Contains('LAPLACE_PLATFORM_OWNER_POLICY')){throw 'An absent policy must remain absent.'}
$policy=[pscustomobject]@{version=1;owners=@([pscustomobject]@{provider='google';issuer='https://accounts.google.com';subject='fixture-private-subject';client_id='fixture';verified_at='2025-01-01T00:00:00Z'})}
$records.identity | Add-Member platform_owner_policy $policy
$environment=Get-IisEnvironment $iis $records
if(($environment.LAPLACE_PLATFORM_OWNER_POLICY | ConvertFrom-Json).owners[0].subject -cne 'fixture-private-subject'){throw 'The private owner policy was not transported exactly.'}
$migrationIdentity=Get-IisIdentityEnvironment $records.identity
if(@($migrationIdentity.Keys | Where-Object {$_ -match 'SECRET'}).Count){throw 'Migration owner policy transport included OAuth secrets.'}
$policy.owners[0].client_id='different-client'
Denied {Get-IisEnvironment $iis $records} 'different provider client'
$policy.owners[0].client_id='fixture'
$iis.environment | Add-Member LAPLACE_PLATFORM_OWNER_POLICY 'must-not-be-public'
Denied {Get-IisEnvironment $iis $records} 'Public environment'
$iis.environment.PSObject.Properties.Remove('LAPLACE_PLATFORM_OWNER_POLICY')
$policy.owners=@()
if(($environment=(Get-IisEnvironment $iis $records)).LAPLACE_PLATFORM_OWNER_POLICY -cne '{"version":1,"owners":[]}'){throw 'Explicit empty owner policy was not retained.'}
$records.identity.PSObject.Properties.Remove('platform_owner_policy')
$environment=Get-IisEnvironment $iis $records
$ownerFile=Get-IisOwnerPolicyPath $iis
$null=New-Item -ItemType Directory -Path (Split-Path -Parent $ownerFile)
$policy.owners=@([pscustomobject]@{provider='google';issuer='https://accounts.google.com';subject='file-private-subject';client_id='fixture';verified_at='2025-01-01T00:00:00Z'})
Iis-Json $policy | Set-Content -LiteralPath $ownerFile
$iis | Add-Member platform_owner_policy_file $ownerFile
$environment=Get-IisEnvironment $iis $records
if($environment.LAPLACE_PLATFORM_OWNER_POLICY_FILE -cne $ownerFile -or $environment.Contains('LAPLACE_PLATFORM_OWNER_POLICY')){throw 'Runtime must read the canonical file without stale inline policy.'}
$privateValues=Get-IisInstallationIdentityEnvironment $iis $records.identity
if(($privateValues.LAPLACE_PLATFORM_OWNER_POLICY | ConvertFrom-Json).owners[0].subject -cne 'file-private-subject'){throw 'Migration policy did not come from the canonical file.'}
Remove-Item -LiteralPath $ownerFile
Denied {Get-IisEnvironment $iis $records} 'owner policy file is missing'
$iis.PSObject.Properties.Remove('platform_owner_policy_file')
$environment=Get-IisEnvironment $iis $records
foreach($name in 'LAPLACE_DB','LAPLACE_APP_DB'){
    $parsed=Parsed $environment[$name]
    if($parsed.ContainsKey('Password') -or $parsed['Host'] -ne $env:COMPUTERNAME -or $parsed['SSL Mode'] -ne 'VerifyFull'){throw 'IIS did not render local password-free connections.'}
}
if((Parsed $environment.LAPLACE_DB)['Database'] -eq (Parsed $environment.LAPLACE_APP_DB)['Database']){throw 'IIS application and substrate database boundaries collapsed.'}
$iis.database.host='hart-server'
Denied {Get-IisCredentialReferences $iis} 'requires this machine'
$iis.database.authentication='scram'
$iis.records | Add-Member substrate 'kv/data/fixture/substrate'
$iis.records | Add-Member appdb 'kv/data/fixture/appdb'
$iis.database.PSObject.Properties.Remove('authentication')
if(@(Get-IisCredentialReferences $iis).Count -ne 6){throw 'Existing password-based declarations no longer select their six records.'}
foreach($kind in 'substrate','appdb'){
    $prefix=if($kind -eq 'substrate'){'substrate'}else{'application'}
    $records | Add-Member $kind ([pscustomobject]@{database_host='hart-server';port=5432;database=$iis.database.("${prefix}_database");role=$iis.database.("${prefix}_role");password=('c'*64)})
}
$environment=Get-IisEnvironment $iis $records
if((Parsed $environment.LAPLACE_DB)['Password'] -ne ('c'*64) -or (Parsed $environment.LAPLACE_APP_DB)['Password'] -ne ('c'*64)){throw 'Existing IIS credential rendering changed.'}
'Windows connection and IIS rendering checks passed: local password-free selection, remote-target refusal, scoped credentials, separate application/substrate databases, and TLS requirements. No live database was changed.'
