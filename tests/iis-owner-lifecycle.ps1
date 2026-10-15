#Requires -Version 7.5
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. "$root/deploy/windows/iis-runtime.ps1"
. "$root/deploy/windows/iis-owner-lifecycle.ps1"
function Assert([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
$work=Join-Path ([IO.Path]::GetTempPath()) ('iis-owner-lifecycle-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path "$work/state/platform-owners"
$config=[pscustomobject]@{directory=$work;origin='https://fixture.invalid';address='https://bao.invalid';records=[pscustomobject]@{identity='kv/data/identity'}}
$identity=[pscustomobject]@{google_client_id='google-fixture';microsoft_client_id='microsoft-fixture'}
$declaration=[pscustomobject]@{platform_access=[pscustomobject]@{browser_origin=$config.origin;administrators=@(
    [pscustomobject]@{provider='microsoft';issuer='https://issuer.invalid';subject='microsoft-owner'},
    [pscustomobject]@{provider='google';issuer='https://accounts.google.com';subject='google-owner'}
)}}
$fixtureBindings=@($declaration.platform_access.administrators | ForEach-Object {
    [pscustomobject]@{provider=$_.provider;issuer=$_.issuer;subject=$_.subject;last_login_at='2025-01-01T00:00:00Z'}
})
$first=Sync-IisPlatformOwnerPolicy $config $identity $declaration {$fixtureBindings}
Assert ($first.changed -and $first.owner_count -eq 2) 'Initial setup did not enroll both declared owners.'
$path=Get-IisOwnerPolicyPath $config
$hash=(Get-FileHash $path).Hash;$modified=(Get-Item $path).LastWriteTimeUtc
$again=Sync-IisPlatformOwnerPolicy $config $identity $declaration {throw 'The application database was dropped.'}
Assert (-not $again.changed -and $again.owner_count -eq 2) 'Repair depended on the dropped application database.'
Assert ((Get-FileHash $path).Hash -ceq $hash -and (Get-Item $path).LastWriteTimeUtc -eq $modified) 'Repeat setup rewrote the policy.'
Write-Host 'PASS initial enrollment, repeat setup, stable bytes/timestamp, and missing database reuse'

$identity | Add-Member platform_owner_policy ([pscustomobject]@{owners=@($first.policy.owners | Sort-Object provider -Descending);version=1})
$same=Sync-IisPlatformOwnerPolicy $config $identity $declaration {throw 'Unexpected binding query.'}
$values=Get-IisInstallationIdentityEnvironment $config $identity
Assert (-not $same.changed -and $values.Contains('LAPLACE_PLATFORM_OWNER_POLICY')) 'Object/owner ordering changed authority.'
Write-Host 'PASS policy comparison ignores JSON property and owner ordering'
$identity.PSObject.Properties.Remove('platform_owner_policy')

foreach($failure in 'wrong-origin','duplicate','unknown-binding','future-verification','client-change'){
    $candidate=Iis-Json $declaration | ConvertFrom-Json
    $candidateIdentity=Iis-Json $identity | ConvertFrom-Json
    $candidateBindings=Iis-Json $fixtureBindings | ConvertFrom-Json
    switch($failure){
        'wrong-origin' {$candidate.platform_access.browser_origin='https://other.invalid'}
        'duplicate' {$candidate.platform_access.administrators+=@($candidate.platform_access.administrators[0])}
        'unknown-binding' {$candidate.platform_access.administrators[0].subject='unverified'}
        'future-verification' {$candidate.platform_access.administrators[0].subject='future';$candidateBindings[0].subject='future';$candidateBindings[0].last_login_at='2999-01-01T00:00:00Z'}
        'client-change' {$candidateIdentity.google_client_id='different-client'}
    }
    $failed=$false
    try{$null=Sync-IisPlatformOwnerPolicy $config $candidateIdentity $candidate {$candidateBindings}}catch{$failed=$true}
    Assert $failed "Invalid authority accepted: $failure"
    Assert ((Get-FileHash $path).Hash -ceq $hash) "Failure changed persisted authority: $failure"
}
Write-Host 'PASS wrong origin, duplicate, unknown, future, and wrong-client bindings preserve existing policy'

$secondRoot=Join-Path $work 'restored'
$null=New-Item -ItemType Directory -Path "$secondRoot/state/platform-owners"
$config.directory=$secondRoot
$identity | Add-Member platform_owner_policy $first.policy
$restored=Sync-IisPlatformOwnerPolicy $config $identity $declaration {throw 'No application database exists.'}
Assert ($restored.changed -and $restored.owner_count -eq 2) 'Verified workload policy did not bootstrap a fresh installation.'
Write-Host 'PASS workload policy restores a fresh installation without an application database'

$script:requests=[Collections.Generic.List[string]]::new()
function Invoke-RestMethod {
    param($Uri,$Method,$ContentType,$Body,$TimeoutSec,$Headers)
    $script:requests.Add($Uri)
    if($Uri.EndsWith('/auth/approle/login')){
        $login=$Body | ConvertFrom-Json
        Assert ($login.role_id -ceq 'role-fixture' -and $login.secret_id -ceq 'secret-fixture') 'Wrong workload enrollment.'
        return @{auth=@{client_token='token-fixture'}}
    }
    Assert ($Headers['X-Vault-Token'] -ceq 'token-fixture') 'Wrong workload token.'
    if($Uri.EndsWith('/auth/token/revoke-self')){return}
    if($script:readFailure){throw 'Private transport failure.'}
    return @{data=@{data=$identity}}
}
$enrollment=[pscustomobject]@{role_id='role-fixture';secret_id='secret-fixture'}
$record=Read-IisWorkloadIdentity $config "$work/no-personal-enrollment" $enrollment
Assert ($record.google_client_id -ceq 'google-fixture' -and $requests.Count -eq 3 -and $requests[2].EndsWith('/revoke-self')) 'Workload identity was not read and its temporary token revoked.'
$requests.Clear();$script:readFailure=$true;$failed=$false
try{$null=Read-IisWorkloadIdentity $config "$work/no-personal-enrollment" $enrollment}catch{$failed=$true}
Assert ($failed -and $requests.Count -eq 3 -and $requests[2].EndsWith('/revoke-self')) 'Read failure did not revoke the workload token.'
Write-Host 'PASS setup uses workload AppRole without personal DPAPI and revokes temporary tokens on success/failure'
