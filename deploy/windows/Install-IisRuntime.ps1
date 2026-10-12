#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installation,[string]$Enrollment,[ValidateSet('Install','Render','Verify')][string]$Operation='Install',[switch]$Plan)
. "$PSScriptRoot/iis-runtime.ps1"
$c=Read-IisInstallation $Installation
$root=$c.directory;$private=Join-Path $root 'agent';$state=Join-Path $root 'state';$web=Join-Path $root 'web'
if($Plan){$c | ConvertTo-Json -Depth 12;return}
if($Operation -eq 'Verify'){Test-IisEndpoint $c -ApiKey $env:LAPLACE_API_KEY | ConvertTo-Json;return}
if(-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'IIS, machine certificates and workload service installation require Administrator PowerShell.'}
Add-Type -Path "$env:windir/System32/inetsrv/Microsoft.Web.Administration.dll"
function Protect-IisDirectory([string]$Path,[string]$PoolRights='',[string]$DeployerRights='') {
    $null=New-Item -ItemType Directory -Path $Path -Force
    $acl=Get-Acl -LiteralPath $Path;$acl.SetAccessRuleProtection($true,$false)
    foreach($rule in @($acl.Access)){$null=$acl.RemoveAccessRuleSpecific($rule)}
    foreach($sid in 'S-1-5-18','S-1-5-32-544'){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    if($PoolRights){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(('IIS APPPOOL\'+$c.pool),$PoolRights,'ContainerInherit,ObjectInherit','None','Allow'))}
    if($DeployerRights){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($c.deployer_sid),$DeployerRights,'ContainerInherit,ObjectInherit','None','Allow'))}
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($Path),$acl)
}
if($Operation -eq 'Render') {
    $records=Get-Content -LiteralPath (Join-Path $private 'candidate.json') -Raw | ConvertFrom-Json
    $values=Get-IisEnvironment $c $records
    if($records.chess.control_token -notmatch '^[a-f0-9]{64}$'){throw 'Missing Lichess controller client credential.'}
    Write-IisAtomic (Join-Path $state 'control.token') $records.chess.control_token
    Write-IisAtomic (Join-Path $state 'chess-client.json') (Iis-Json @{url=$c.chess.url;operators=$c.chess.operators;tokenFile=(Join-Path $state 'control.token')})
    $certBytes=[Convert]::FromBase64String($records.certificate.pfx_base64)
    $certDigest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certBytes))
    $receiptPath=Join-Path $private 'render-receipt.json'
    $old=if(Test-Path $receiptPath){Get-Content $receiptPath -Raw | ConvertFrom-Json}else{$null}
    $certificate=$null
    if($old -and $old.certificate_digest -eq $certDigest){$certificate=Get-Item "Cert:/LocalMachine/My/$($old.certificate)" -ErrorAction SilentlyContinue}
    if(-not $certificate -or -not $certificate.HasPrivateKey) {
        $pfx=Join-Path $private 'certificate.pfx';[IO.File]::WriteAllBytes($pfx,$certBytes)
        $password=ConvertTo-SecureString $records.certificate.password -AsPlainText -Force
        try{$certificate=Import-PfxCertificate -FilePath $pfx -Password $password -CertStoreLocation Cert:/LocalMachine/My | Where-Object HasPrivateKey | Select-Object -First 1}
        finally{$password.Dispose();Remove-Item -LiteralPath $pfx -Force}
    }
    $hostname=([Uri]$c.origin).DnsSafeHost;$wildcard='*.'+$hostname.Substring($hostname.IndexOf('.')+1)
    $names=@($certificate.DnsNameList | ForEach-Object {$_.Unicode})
    if($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date) -or ($hostname -notin $names -and $wildcard -notin $names)){throw 'The delivered certificate does not cover the IIS origin.'}
    $manager=[Microsoft.Web.Administration.ServerManager]::new()
    try {
        $pool=$manager.ApplicationPools[$c.pool];$site=$manager.Sites[$c.site]
        if(-not $pool -or -not $site){throw 'The declared IIS pool/site is missing.'}
        $collection=$pool.GetCollection('environmentVariables')
        $current=@{};foreach($item in $collection){$current[[string]$item['name']]=[string]$item['value']}
        $changed=$current.Count -ne $values.Count
        foreach($key in $values.Keys){if($current[$key] -cne $values[$key]){$changed=$true}}
        if($changed){
            $collection.Clear()
            foreach($key in $values.Keys){$entry=$collection.CreateElement('add');$entry['name']=$key;$entry['value']=$values[$key];$collection.Add($entry)}
        }
        $binding=@($site.Bindings | Where-Object {$_.Protocol -eq 'https' -and $_.Host -eq $hostname})
        if($binding.Count -ne 1){throw 'The declared HTTPS binding is missing or ambiguous.'}
        $certChanged=(-not $binding[0].CertificateHash -or [BitConverter]::ToString($binding[0].CertificateHash) -ne [BitConverter]::ToString($certificate.GetCertHash()))
        if($certChanged){$binding[0].CertificateHash=$certificate.GetCertHash();$binding[0].CertificateStoreName='My';$binding[0].SslFlags=[Microsoft.Web.Administration.SslFlags]::Sni}
        if($changed -or $certChanged){$manager.CommitChanges()}
        if($changed -and $pool.State -eq 'Started'){$null=$pool.Recycle()}
    } finally {$manager.Dispose()}
    Write-IisAtomic $receiptPath (Iis-Json @{rendered_utc=[DateTime]::UtcNow.ToString('o');source='openbao-agent';pool=$c.pool;environment_changed=$changed;certificate=$certificate.Thumbprint;certificate_digest=$certDigest})
    return
}
$selection=Join-Path $web 'active.json'
if(Test-Path -LiteralPath $selection){
    $active=Get-Content -LiteralPath $selection -Raw|ConvertFrom-Json
    if($active.revision -notmatch '^[a-f0-9]{7,64}$'){throw 'The installed release selection is invalid.'}
    $c.revision=$active.revision;$c.publish=Join-Path $web ('releases/'+$c.revision)
}
$manifest=Test-IisPackage $c.publish
if($manifest.revision -ne $c.revision){throw 'Package revision differs from the installation declaration.'}
if(-not(Test-Path -LiteralPath $c.bao -PathType Leaf)){throw 'Declared OpenBao executable is missing.'}
if($Enrollment){
    $secure=ConvertTo-SecureString (Get-Content -LiteralPath $Enrollment -Raw).Trim()
    try{$e=([Net.NetworkCredential]::new('', $secure)).Password | ConvertFrom-Json}finally{$secure.Dispose()}
    if($e.role -ne $c.role -or $e.address -ne $c.address -or $e.host -ne $c.host -or -not $e.role_id -or -not $e.secret_id){throw 'Workload enrollment differs from the declared installation.'}
}elseif(-not(Test-Path (Join-Path $private 'secret-id'))){throw 'Initial setup requires the scoped workload enrollment.'}
foreach($file in 'Install-IisRuntime.ps1','iis-runtime.ps1','Publish-IisRuntime.ps1'){
    if(-not(Test-Path (Join-Path $PSScriptRoot $file))){throw "Missing installation script: $file"}
}
if($c.environment.LAPLACE_DATA -and -not(Test-Path -LiteralPath $c.environment.LAPLACE_DATA -PathType Container)){throw 'Declared source data directory is missing.'}
$manager=[Microsoft.Web.Administration.ServerManager]::new()
try {
    if(-not @($manager.GetApplicationHostConfiguration().GetSection('system.webServer/globalModules').GetCollection() | Where-Object {$_['name'] -eq 'AspNetCoreModuleV2'}).Count){throw 'The ASP.NET Core Hosting Bundle is not installed.'}
    if($c.legacy_site){
        $legacy=$manager.Sites[$c.legacy_site]
        if($legacy -and $legacy.Applications['/'].VirtualDirectories['/'].PhysicalPath -ne $c.legacy_path){throw 'Legacy site has changed; refusing to stop a different installation.'}
    }
    $pool=$manager.ApplicationPools[$c.pool]
    if(-not $pool){$pool=$manager.ApplicationPools.Add($c.pool)}
    $pool.ManagedRuntimeVersion='';$pool.Enable32BitAppOnWin64=$false
    $pool.ProcessModel.IdentityType=[Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity
    $pool.ProcessModel.LoadUserProfile=$true;$pool.ProcessModel.IdleTimeout=[TimeSpan]::Zero
    $pool.StartMode=[Microsoft.Web.Administration.StartMode]::AlwaysRunning
    $pool.Recycling.PeriodicRestart.Time=[TimeSpan]::Zero
    $manager.CommitChanges()
} finally {$manager.Dispose()}
Protect-IisDirectory $root 'ReadAndExecute' 'ReadAndExecute'
Protect-IisDirectory $private
Protect-IisDirectory $state 'ReadAndExecute'
foreach($leaf in 'keys','logs','output'){Protect-IisDirectory (Join-Path $state $leaf) 'Modify'}
Protect-IisDirectory $web 'ReadAndExecute' 'Modify'
Protect-IisDirectory (Join-Path $root 'receipts') 'ReadAndExecute' 'Modify'
if($c.environment.LAPLACE_DATA){
    if(-not(Test-Path -LiteralPath $c.environment.LAPLACE_DATA -PathType Container)){throw 'Declared source data directory is missing.'}
    $acl=Get-Acl -LiteralPath $c.environment.LAPLACE_DATA
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(('IIS APPPOOL\'+$c.pool),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $c.environment.LAPLACE_DATA -AclObject $acl
}
Write-IisAtomic (Join-Path $state 'postgres-root.crt') $c.database.certificate
$release=Join-Path $web ('releases/'+$c.revision)
if(Test-Path -LiteralPath $release){$null=Test-IisPackage $release}else{
    $null=New-Item -ItemType Directory -Path $release -Force
    Copy-Item -Path (Join-Path $c.publish '*') -Destination $release -Recurse -Force
    $null=Test-IisPackage $release
}
$webConfig=Join-Path $web 'web.config'
$oldConfig=if(Test-Path $webConfig){Get-Content $webConfig -Raw}else{$null}
Write-IisAtomic $webConfig (Get-IisWebConfig $c.revision (Join-Path $state 'logs'))
foreach($file in 'Install-IisRuntime.ps1','iis-runtime.ps1','Publish-IisRuntime.ps1'){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $private $file) -Force}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'iis-runtime.ps1') -Destination (Join-Path $root 'iis-runtime.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Publish-IisRuntime.ps1') -Destination (Join-Path $root 'Publish-IisRuntime.ps1') -Force
$service=$c.pool+'-credentials'
if(Get-Service $service -ErrorAction SilentlyContinue){Stop-Service $service}
$bao=Join-Path $private 'bao.exe'
if([IO.Path]::GetFullPath($c.bao) -ne [IO.Path]::GetFullPath($bao)){Copy-Item -LiteralPath $c.bao -Destination $bao -Force}
$c.bao=$bao;$c.publish=$release
Write-IisAtomic (Join-Path $root 'installation.json') (Iis-Json $c)
Write-IisAtomic (Join-Path $private 'installation.json') (Iis-Json $c)
if($Enrollment){
    Write-IisAtomic (Join-Path $private 'role-id') $e.role_id
    Write-IisAtomic (Join-Path $private 'secret-id') $e.secret_id
}
if(-not(Test-Path (Join-Path $private 'secret-id'))){throw 'Initial setup requires the scoped workload enrollment.'}
$members=@();foreach($entry in $c.records.PSObject.Properties){$members+=('"'+$entry.Name+'":{{ with secret "'+$entry.Value+'" }}{{ .Data.data | toJSON }}{{ end }}')}
$template='{'+($members -join ',')+'}'
$agent=@"
log_file = $(Iis-Json (Join-Path $private 'openbao.log'))
log_rotate_bytes = 10485760
log_rotate_max_files = 3
vault { address = $(Iis-Json $c.address) }
auto_auth { method "approle" { config = {
role_id_file_path = $(Iis-Json (Join-Path $private 'role-id'))
secret_id_file_path = $(Iis-Json (Join-Path $private 'secret-id'))
remove_secret_id_file_after_reading = false
} } }
template_config { static_secret_render_interval = "1m"
exit_on_retry_failure = true }
template { contents = $(Iis-Json $template)
destination = $(Iis-Json (Join-Path $private 'candidate.json'))
backup = false
error_on_missing_key = true
exec { command = $(Iis-Json @((Get-Command pwsh).Source,'-NoProfile','-File',(Join-Path $private 'Install-IisRuntime.ps1'),'-Installation',(Join-Path $private 'installation.json'),'-Operation','Render'))
timeout = "45s" }
}
"@
Write-IisAtomic (Join-Path $private 'agent.hcl') $agent
$manager=[Microsoft.Web.Administration.ServerManager]::new()
try {
    $uri=[Uri]$c.origin;$binding='*:'+$uri.Port+':'+$uri.DnsSafeHost
    $site=$manager.Sites[$c.site]
    if(-not $site){$site=$manager.Sites.Add($c.site,'https',$binding,$web);$site.ServerAutoStart=$false}
    elseif($site.Applications['/'].VirtualDirectories['/'].PhysicalPath -ne $web){throw 'Existing site points outside the declared installation.'}
    $site.Applications['/'].ApplicationPoolName=$c.pool
    if(-not @($site.Bindings | Where-Object {$_.Protocol -eq 'https' -and $_.BindingInformation -eq $binding}).Count){$null=$site.Bindings.Add($binding,'https')}
    foreach($https in $site.Bindings | Where-Object {$_.Protocol -eq 'https' -and $_.BindingInformation -eq $binding}){$https.SslFlags=[Microsoft.Web.Administration.SslFlags]::Sni}
    $anonymous=$manager.GetApplicationHostConfiguration().GetSection('system.webServer/security/authentication/anonymousAuthentication',$c.site)
    $anonymous['enabled']=$true;$anonymous['userName']=''
    $manager.CommitChanges()
} finally {$manager.Dispose()}
$binary='"'+$bao+'" agent -config="'+(Join-Path $private 'agent.hcl')+'"'
if(Get-Service $service -ErrorAction SilentlyContinue){
    $changed=Get-CimInstance Win32_Service -Filter "Name='$service'" | Invoke-CimMethod -MethodName Change -Arguments @{PathName=$binary;StartMode='Automatic'}
    if($changed.ReturnValue -ne 0){throw 'Could not update the credential service.'}
}else{New-Service -Name $service -BinaryPathName $binary -StartupType Automatic | Out-Null}
$null=& sc.exe failure $service reset= 86400 actions= restart/5000/restart/15000/restart/60000
if($LASTEXITCODE){throw 'Could not configure credential service recovery.'}
# Require a new successful render by the installed agent, not an old candidate.
Remove-Item -LiteralPath (Join-Path $private 'candidate.json') -Force -ErrorAction SilentlyContinue
$began=[DateTime]::UtcNow
Start-Service $service
$deadline=(Get-Date).AddSeconds(60)
$rendered=$false
do {
    $receiptPath=Join-Path $private 'render-receipt.json'
    if((Test-Path $receiptPath) -and ([DateTimeOffset](Get-Content $receiptPath -Raw|ConvertFrom-Json).rendered_utc).UtcDateTime -ge $began){$rendered=$true;break}
    Start-Sleep -Seconds 1
} while((Get-Date) -lt $deadline)
if(-not $rendered){throw 'The installed workload agent did not render current credentials; inspect agent/openbao.log.'}
$manager=[Microsoft.Web.Administration.ServerManager]::new()
try{if($manager.ApplicationPools[$c.pool].State -ne 'Started'){$null=$manager.ApplicationPools[$c.pool].Start()};$manager.Sites[$c.site].ServerAutoStart=$true;$manager.CommitChanges();if($manager.Sites[$c.site].State -ne 'Started'){$null=$manager.Sites[$c.site].Start()}}finally{$manager.Dispose()}
try {
    $verified=$null
    for($attempt=0;$attempt -lt 12;$attempt++){
            try{$records=Get-Content (Join-Path $private 'candidate.json') -Raw|ConvertFrom-Json;$verified=Test-IisEndpoint $c -OperatorToken $records.api.operator_token;break}catch{if($attempt -eq 11){throw};Start-Sleep -Seconds 2}
    }
    # Verify the installed process again after recycling its actual application pool.
    $manager=[Microsoft.Web.Administration.ServerManager]::new()
    try{$null=$manager.ApplicationPools[$c.pool].Recycle()}finally{$manager.Dispose()}
    for($attempt=0;$attempt -lt 12;$attempt++){
        try{$verified=Test-IisEndpoint $c -OperatorToken $records.api.operator_token;break}catch{if($attempt -eq 11){throw};Start-Sleep -Seconds 2}
    }
    $verified.pool_restart='passed'
}catch{
    if($oldConfig){Write-IisAtomic $webConfig $oldConfig}
    throw
}
# Retire only the named, already inspected legacy route after its replacement works.
if($c.legacy_site){$manager=[Microsoft.Web.Administration.ServerManager]::new();try{$legacy=$manager.Sites[$c.legacy_site];if($legacy){if($legacy.State -eq 'Started'){$null=$legacy.Stop()};$legacy.ServerAutoStart=$false;$manager.CommitChanges()}}finally{$manager.Dispose()}}
$machine=@(Get-Content -LiteralPath $c.machine_env | Where-Object {$_ -notmatch '^LAPLACE_(IIS_SITE|IIS_POOL|IIS_API|IIS_INSTALLATION|PUBLIC_BASE_URL|IIS_CERTIFICATE_THUMBPRINT)='})
$receipt=Get-Content (Join-Path $private 'render-receipt.json') -Raw | ConvertFrom-Json
$settings=@{LAPLACE_IIS_SITE=$c.site;LAPLACE_IIS_POOL=$c.pool;LAPLACE_IIS_API=$web;LAPLACE_PUBLIC_BASE_URL=$c.origin;LAPLACE_IIS_INSTALLATION=(Join-Path $root 'installation.json');LAPLACE_IIS_CERTIFICATE_THUMBPRINT=$receipt.certificate}
foreach($key in $settings.Keys){$machine+=$key+'='+$settings[$key]}
Write-IisAtomic $c.machine_env (($machine -join "`r`n")+"`r`n")
$verified.revision=$c.revision;$verified.credential_source='openbao-agent';$verified.legacy_site_retired=$c.legacy_site
$receiptPath=Join-Path $root ('receipts/setup-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'.json')
Write-IisAtomic $receiptPath (Iis-Json $verified)
Write-IisAtomic $selection (Iis-Json @{revision=$c.revision})
if($Enrollment){Remove-Item -LiteralPath $Enrollment -Force}
Write-Host "Installed and verified $($c.origin). Receipt: $receiptPath"
