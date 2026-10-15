#Requires -Version 7.5
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Installation,
    [Parameter(Mandatory)][string]$OwnerDeclaration,
    [Parameter(Mandatory)][string]$AccessConfiguration,
    [string]$PolicyFile
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/iis-runtime.ps1"
$c=Read-IisInstallation $Installation
$declared=Get-Content -LiteralPath $OwnerDeclaration -Raw | ConvertFrom-Json
$access=Get-Content -LiteralPath $AccessConfiguration -Raw | ConvertFrom-Json
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if($identity.User.Value -cne $declared.windows_sid -or $identity.Name -cne $declared.windows_identity){throw 'Run owner policy enrollment as the declared Windows operator.'}
if(($access.identity_reference -replace '^kv/','kv/data/') -cne $c.records.identity -or $access.client -ine $c.host -or
    $access.bao_address.TrimEnd('/') -cne $c.address.TrimEnd('/') -or $access.authentication -cne 'sspi' -or
    $access.database_host -ine $c.database.host -or $access.application_database -cne $c.database.application_database -or
    $access.port -ne $c.database.port){throw 'Owner enrollment declarations select different installations.'}
$expected=@($declared.platform_access.administrators)
if(-not $expected.Count){throw 'No owner bindings are declared.'}
$destination=Get-IisOwnerPolicyPath $c
$directory=Split-Path -Parent $destination
if(-not(Test-Path -LiteralPath $directory -PathType Container)){throw 'Install the owner policy directory through the existing IIS runtime setup before enrollment.'}
$acl=Get-Acl -LiteralPath $directory
if(-not $acl.AreAccessRulesProtected -or ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Owner policy directory must use the installed private ACL.'}
$poolSid=([Security.Principal.NTAccount]::new('IIS APPPOOL\'+$c.pool)).Translate([Security.Principal.SecurityIdentifier]).Value
$allowed=@('S-1-5-18','S-1-5-32-544',$declared.windows_sid,$poolSid)
foreach($rule in $acl.Access){
    if($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowed){throw 'Owner policy directory grants access outside the declared installation.'}
}
. (Join-Path (Split-Path -Parent $AccessConfiguration) 'credentials.ps1')
$token=$null
try{
    $token=New-DevelopmentBaoSession $access
    $record=(Invoke-DevelopmentBao $access GET $c.records.identity $token).data.data
}finally{
    try{if($token){$null=Invoke-DevelopmentBao $access POST 'auth/token/revoke-self' $token @{}}}
    finally{$token=$null}
}
if($PolicyFile){
    try{$policy=Get-Content -LiteralPath $PolicyFile -Raw | ConvertFrom-Json -DateKind String}catch{throw 'The selected private policy file is invalid.'}
}else{
    $saved=@{}
    foreach($entry in @(Get-ChildItem Env: | Where-Object Name -Like 'PG*')){$saved[$entry.Name]=$entry.Value;Remove-Item -LiteralPath ('Env:'+$entry.Name)}
    try{
        $env:PGSSLMODE='verify-full';$env:PGSSLROOTCERT=$access.root_certificate;$env:PGREQUIREAUTH='sspi';$env:PGCONNECT_TIMEOUT='10'
        $sql=@'
SELECT coalesce(json_agg(row_to_json(i)), '[]'::json) FROM (
 SELECT provider,issuer,subject,last_login_at FROM app.external_identities ORDER BY identity_id LIMIT 50001
) i;
'@
        $raw=$sql | & $access.psql -X -w -h $access.database_host -p $access.port -U $access.admin_role -d $access.application_database -Atq -v ON_ERROR_STOP=1 -f - 2>&1
        if($LASTEXITCODE){throw 'Owner policy enrollment could not read the declared application identity bindings.'}
        try{$bindings=($raw -join "`n") | ConvertFrom-Json}catch{throw 'Owner identity readback has an invalid contract.'}
        if(@($bindings).Count -ge 50001){throw 'Owner identity readback exceeds its bounded result limit.'}
        $owners=@(foreach($person in $expected){
            $match=@($bindings | Where-Object {$_.provider -ceq $person.provider -and $_.issuer -ceq $person.issuer -and $_.subject -ceq $person.subject})
            if($match.Count -ne 1 -or -not $match[0].last_login_at){throw 'Each declared owner needs one previously authenticated exact provider binding.'}
            @{provider=$person.provider;issuer=$person.issuer;subject=$person.subject;client_id=$record.("$($person.provider)_client_id");verified_at=([DateTimeOffset]$match[0].last_login_at).ToString('o')}
        })
        $policy=[pscustomobject]@{version=1;owners=$owners}
    }finally{
        $raw=$null;$bindings=$null
        foreach($entry in @(Get-ChildItem Env: | Where-Object Name -Like 'PG*')){Remove-Item -LiteralPath ('Env:'+$entry.Name)}
        foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
    }
}
if(@($policy.owners).Count -ne $expected.Count){throw 'Owner policy must contain exactly the declared operator bindings.'}
foreach($person in $expected){
    $matches=@($policy.owners | Where-Object {$_.provider -ceq $person.provider -and $_.issuer -ceq $person.issuer -and $_.subject -ceq $person.subject})
    if($matches.Count -ne 1){throw 'Owner policy differs from the declared operator bindings.'}
}
$record | Add-Member -NotePropertyName platform_owner_policy -NotePropertyValue $policy -Force
$null=Get-IisIdentityEnvironment $record
$serialized=Iis-Json $policy
$lock=[IO.File]::Open((Join-Path $directory '.enrollment.lock'),'OpenOrCreate','ReadWrite','None')
try{
    if(Test-Path -LiteralPath $destination){
        $file=Get-Item -LiteralPath $destination
        if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Owner policy destination cannot be a link.'}
        $previous=Get-Content -LiteralPath $destination -Raw
        if($previous -ceq $serialized){
            $changed=$false
        }else{
            Write-IisAtomic (Join-Path $directory 'policy.previous.json') $previous
            Write-IisAtomic $destination $serialized
            $changed=$true
        }
    }else{Write-IisAtomic $destination $serialized;$changed=$true}
    if([IO.File]::ReadAllText($destination) -cne $serialized){throw 'The persisted owner policy did not match readback.'}
}finally{$lock.Dispose()}
[ordered]@{
    host=$c.host;owner_policy='persisted-and-read-back';owner_count=@($policy.owners).Count;changed=$changed
    runtime_activation='publish or restart the installed application with owner-policy support'
} | ConvertTo-Json
