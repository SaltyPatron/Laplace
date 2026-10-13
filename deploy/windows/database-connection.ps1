# Windows PostgreSQL connection policy shared by the installed CLI and IIS runtime.
function Get-LaplaceWindowsDatabaseAuthentication {
    param([Parameter(Mandatory)][Collections.IDictionary]$Database)
    $authentication=if($Database['authentication']){[string]$Database['authentication']}else{'scram'}
    if($authentication -notin @('scram','sspi')){throw 'Unsupported PostgreSQL authentication method.'}
    if($authentication -eq 'sspi' -and (-not $IsWindows -or $Database['host'] -notin @($env:COMPUTERNAME,'localhost','127.0.0.1','::1'))){
        throw 'Windows development SSPI requires this machine as the database host.'
    }
    $authentication
}
function New-LaplaceWindowsDatabaseConnection {
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Database,
        $CredentialRecord
    )
    $hostName=[string]$Database['host']
    $databaseName=[string]$Database['database']
    $role=[string]$Database['role']
    $authentication=Get-LaplaceWindowsDatabaseAuthentication $Database
    if($hostName -notmatch '^[a-zA-Z0-9.:-]+$' -or $databaseName -notmatch '^[a-zA-Z0-9_-]+$' -or $role -notmatch '^[a-zA-Z0-9_-]+$'){
        throw 'Invalid PostgreSQL host, database or role.'
    }
    if([int]$Database['port'] -lt 1 -or [int]$Database['port'] -gt 65535 -or [int]$Database['pool_max'] -lt 1){
        throw 'Invalid PostgreSQL port or connection budget.'
    }
    $trust=[string]$Database['root_certificate']
    if(-not [IO.Path]::IsPathFullyQualified($trust) -or -not(Test-Path -LiteralPath $trust -PathType Leaf)){
        throw 'The selected PostgreSQL trust certificate must be an existing absolute file.'
    }
    $connection=[Data.Common.DbConnectionStringBuilder]::new()
    foreach($pair in @{Host=$hostName;Port=[string]$Database['port'];Database=$databaseName;Username=$role;'SSL Mode'='VerifyFull';'Root Certificate'=$trust;'Maximum Pool Size'=[string]$Database['pool_max']}.GetEnumerator()){
        $connection[$pair.Key]=$pair.Value
    }
    switch($authentication){
        'sspi' {
            if($null -ne $CredentialRecord){throw 'SSPI must not consume a database password record.'}
        }
        'scram' {
            if($null -eq $CredentialRecord -or $CredentialRecord.database_host -ne $hostName -or
                $CredentialRecord.database -ne $databaseName -or $CredentialRecord.role -ne $role -or
                [int]$CredentialRecord.port -ne [int]$Database['port'] -or -not $CredentialRecord.password){
                throw 'Database credential selection differs from this installation.'
            }
            $connection['Password']=[string]$CredentialRecord.password
        }
        default {throw 'Unsupported PostgreSQL authentication method.'}
    }
    $connection.get_ConnectionString()
}
