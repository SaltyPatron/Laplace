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
. "$PSScriptRoot/iis-owner-lifecycle.ps1"
if($PolicyFile){
    $policy=Read-IisOwnerJson $PolicyFile
    $record | Add-Member platform_owner_policy $policy -Force
}
$result=Sync-IisPlatformOwnerPolicy $c $record $declared {Read-IisOwnerBindings $c $access}
[ordered]@{
    host=$c.host;owner_policy='persisted-and-read-back';owner_count=$result.owner_count;changed=$result.changed
    runtime_activation='publish or restart the installed application with owner-policy support'
} | ConvertTo-Json
