#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installation,[Parameter(Mandatory)][string]$Destination)
. "$PSScriptRoot/iis-runtime.ps1"
$c=Read-IisInstallation $Installation
function Invoke-Bao([string[]]$Arguments,[string]$InputText='') {
    $info=[Diagnostics.ProcessStartInfo]::new($c.bao)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.Environment['BAO_ADDR']=$c.address
    foreach($arg in $Arguments){$info.ArgumentList.Add($arg)}
    $p=[Diagnostics.Process]::Start($info)
    try {
        $output=$p.StandardOutput.ReadToEndAsync();$errorText=$p.StandardError.ReadToEndAsync()
        $p.StandardInput.Write($InputText);$p.StandardInput.Close()
        if(-not $p.WaitForExit(30000)){$p.Kill($true);throw 'OpenBao enrollment timed out.'}
        if($p.ExitCode){throw 'OpenBao enrollment failed; verify the current enrollment operator policy.'}
        return $output.GetAwaiter().GetResult()
    } finally {$p.Dispose()}
}
$references=@(Get-IisCredentialReferences $c)
$policy=(@($references | ForEach-Object {'path "'+$_.Value+'" { capabilities = ["read"] }'}) -join "`n")
$policy+="`n"+'path "auth/token/lookup-self" { capabilities = ["read"] }'+"`n"+'path "auth/token/renew-self" { capabilities = ["update"] }'+"`n"+'path "auth/token/revoke-self" { capabilities = ["update"] }'
$null=Invoke-Bao @('policy','write',$c.role,'-') $policy
$role=@{token_policies=@($c.role);token_no_default_policy=$true;token_ttl='5m';token_max_ttl='10m';secret_id_ttl=0;secret_id_num_uses=0}
$null=Invoke-Bao @('write',('auth/approle/role/'+$c.role),'-') (Iis-Json $role)
$roleId=(Invoke-Bao @('read','-format=json',('auth/approle/role/'+$c.role+'/role-id'))|ConvertFrom-Json).data.role_id
$secret=(Invoke-Bao @('write','-format=json','-f',('auth/approle/role/'+$c.role+'/secret-id'))|ConvertFrom-Json).data
$e=@{host=$c.host;address=$c.address;role=$c.role;role_id=$roleId;secret_id=$secret.secret_id}
$secure=ConvertTo-SecureString (Iis-Json $e) -AsPlainText -Force
try{Write-IisAtomic $Destination (ConvertFrom-SecureString $secure)}finally{$secure.Dispose()}
$acl=Get-Acl $Destination;$acl.SetAccessRuleProtection($true,$false)
foreach($rule in @($acl.Access)){$acl.RemoveAccessRuleSpecific($rule)}
foreach($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18','S-1-5-32-544')){
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','Allow'))
}
Set-Acl -LiteralPath $Destination -AclObject $acl
Write-Host "Prepared encrypted enrollment for $($c.role); policy reads only the $($references.Count) selected records."
