# Owner authority belongs to the installation, outside the application database.
function Read-IisWorkloadIdentity($Config,[string]$Private,$Enrollment){
    $token=$null
    try{
        if($Enrollment){$roleId=$Enrollment.role_id;$secretId=$Enrollment.secret_id}
        else{
            $roleId=(Get-Content -LiteralPath (Join-Path $Private 'role-id') -Raw).Trim()
            $secretId=(Get-Content -LiteralPath (Join-Path $Private 'secret-id') -Raw).Trim()
        }
        $login=Invoke-RestMethod ($Config.address.TrimEnd('/')+'/v1/auth/approle/login') -Method Post -ContentType application/json -Body (Iis-Json @{role_id=$roleId;secret_id=$secretId}) -TimeoutSec 20
        $token=$login.auth.client_token
        if(-not $token){throw 'No workload token.'}
        $response=Invoke-RestMethod ($Config.address.TrimEnd('/')+'/v1/'+$Config.records.identity) -Headers @{'X-Vault-Token'=$token} -TimeoutSec 20
        $null=Get-IisIdentityEnvironment $response.data.data
        return $response.data.data
    }catch{throw 'The installed workload enrollment could not read its identity configuration.'}
    finally{
        try{if($token){$null=Invoke-RestMethod ($Config.address.TrimEnd('/')+'/v1/auth/token/revoke-self') -Method Post -Headers @{'X-Vault-Token'=$token} -TimeoutSec 20}}
        catch{throw 'The temporary installation workload token could not be revoked.'}
        finally{$token=$null;$roleId=$null;$secretId=$null;$login=$null}
    }
}
function Read-IisOwnerJson([string]$Path){
    $file=Get-Item -LiteralPath $Path -ErrorAction Stop
    if($file.PSIsContainer -or $file.Length -gt 65536 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Owner configuration must be bounded regular JSON.'}
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -DateKind String -ErrorAction Stop
}
function Assert-IisOwnerSelection($Config,$Declaration){
    if($Declaration.platform_access.browser_origin.TrimEnd('/') -cne $Config.origin.TrimEnd('/')){throw 'Owner declaration selects another installation origin.'}
    $expected=@($Declaration.platform_access.administrators)
    if($expected.Count -lt 1 -or $expected.Count -gt 16){throw 'Declare one to sixteen installation owners.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($person in $expected){
        if($person.provider -cnotin @('google','microsoft')){throw 'Invalid declared owner provider.'}
        foreach($field in 'issuer','subject'){
            if(-not ($person.$field -is [string]) -or [string]::IsNullOrWhiteSpace($person.$field) -or $person.$field.Length -gt 4096 -or $person.$field -match '[\x00-\x1f]'){throw 'Invalid declared owner binding.'}
        }
        if(-not $seen.Add((Iis-Json @($person.provider,$person.issuer,$person.subject)))){throw 'Duplicate declared owner binding.'}
    }
}
function Read-IisOwnerBindings($Config,$Access){
    if($Access.authentication -cne 'sspi' -or $Access.database_host -ine $Config.database.host -or
        $Access.port -ne $Config.database.port -or $Access.application_database -cne $Config.database.application_database){throw 'Owner enrollment database differs from the installation.'}
    $saved=@{}
    foreach($entry in @(Get-ChildItem Env: | Where-Object Name -Like 'PG*')){$saved[$entry.Name]=$entry.Value;Remove-Item -LiteralPath ('Env:'+$entry.Name)}
    try{
        $env:PGSSLMODE='verify-full';$env:PGSSLROOTCERT=$Access.root_certificate;$env:PGREQUIREAUTH='sspi';$env:PGCONNECT_TIMEOUT='10'
        $sql="SELECT coalesce(json_agg(row_to_json(i)), '[]'::json) FROM (SELECT provider,issuer,subject,last_login_at FROM app.external_identities ORDER BY identity_id LIMIT 50001) i;"
        $raw=$sql | & $Access.psql -X -w -h $Access.database_host -p $Access.port -U $Access.admin_role -d $Access.application_database -Atq -v ON_ERROR_STOP=1 -f - 2>&1
        if($LASTEXITCODE){throw 'Owner enrollment could not read previously authenticated provider bindings.'}
        try{$bindings=($raw -join "`n") | ConvertFrom-Json -DateKind String}catch{throw 'Invalid owner identity readback.'}
        if(@($bindings).Count -ge 50001){throw 'Owner identity readback exceeds its bounded result limit.'}
        return $bindings
    }finally{
        $raw=$null
        foreach($entry in @(Get-ChildItem Env: | Where-Object Name -Like 'PG*')){Remove-Item -LiteralPath ('Env:'+$entry.Name)}
        foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
    }
}
function Sync-IisPlatformOwnerPolicy($Config,$Identity,$Declaration,[scriptblock]$ReadBindings){
    if($Declaration){Assert-IisOwnerSelection $Config $Declaration}
    $path=Get-IisOwnerPolicyPath $Config
    $directory=Split-Path -Parent $path
    $folder=Get-Item -LiteralPath $directory -ErrorAction Stop
    if(-not $folder.PSIsContainer -or ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Owner policy directory must be the installed private directory.'}
    $lockPath=Join-Path $directory '.enrollment.lock'
    if((Test-Path -LiteralPath $lockPath) -and ((Get-Item -LiteralPath $lockPath).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Owner enrollment lock cannot be a link.'}
    $lock=[IO.File]::Open($lockPath,'OpenOrCreate','ReadWrite','None')
    try{
        $previous=if(Test-Path -LiteralPath $path){Read-IisOwnerJson $path}else{$null}
        $recordPolicy=$Identity.platform_owner_policy
        foreach($candidate in @($previous,$recordPolicy)){
            if($null -eq $candidate){continue}
            $check=Iis-Json $Identity | ConvertFrom-Json -DateKind String
            $check | Add-Member platform_owner_policy $candidate -Force
            $null=Get-IisIdentityEnvironment $check
        }
        $policy=if($previous -and @($previous.owners).Count){$previous}elseif($recordPolicy){$recordPolicy}else{[pscustomobject]@{version=1;owners=@()}}
        if($Declaration){
            $bindings=$null
            $owners=@(foreach($person in $Declaration.platform_access.administrators){
                $known=@($policy.owners | Where-Object {$_.provider -ceq $person.provider -and $_.issuer -ceq $person.issuer -and $_.subject -ceq $person.subject})
                if($known.Count -eq 1){$known[0];continue}
                if($null -eq $bindings){
                    if(-not $ReadBindings){throw 'Initial owner enrollment needs previously authenticated bindings or a verified private policy.'}
                    $bindings=@(& $ReadBindings)
                }
                $match=@($bindings | Where-Object {$_.provider -ceq $person.provider -and $_.issuer -ceq $person.issuer -and $_.subject -ceq $person.subject})
                if($match.Count -ne 1 -or -not $match[0].last_login_at){throw 'Each declared owner needs one previously authenticated exact provider binding.'}
                [pscustomobject]@{provider=$person.provider;issuer=$person.issuer;subject=$person.subject;client_id=$Identity.("$($person.provider)_client_id");verified_at=([DateTimeOffset]$match[0].last_login_at).ToString('o')}
            })
            $policy=[pscustomobject]@{version=1;owners=$owners}
        }
        $check=Iis-Json $Identity | ConvertFrom-Json -DateKind String
        $check | Add-Member platform_owner_policy $policy -Force
        $null=Get-IisIdentityEnvironment $check
        $serialized=ConvertTo-IisCanonicalOwnerPolicy $policy
        if($recordPolicy -and (ConvertTo-IisCanonicalOwnerPolicy $recordPolicy) -cne $serialized){throw 'Installed and credential-record owner policies disagree.'}
        $changed= -not $previous -or (ConvertTo-IisCanonicalOwnerPolicy $previous) -cne $serialized
        if($changed){Write-IisAtomic $path $serialized}
        if((ConvertTo-IisCanonicalOwnerPolicy (Read-IisOwnerJson $path)) -cne $serialized){throw 'Persisted owner policy differs from readback.'}
        [pscustomobject]@{owner_count=@($policy.owners).Count;changed=$changed;policy=$policy}
    }finally{$lock.Dispose()}
}
