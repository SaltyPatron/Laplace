#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installation,[Parameter(Mandatory)][string]$Enrollment,[Parameter(Mandatory)][string]$WorkDirectory,[Parameter(Mandatory)][string]$DeniedRecord,[int]$Port=5197)
. "$PSScriptRoot/iis-runtime.ps1"
$c=Read-IisInstallation $Installation
$manifest=Test-IisPackage $c.publish
$null=New-Item -ItemType Directory -Path $WorkDirectory -Force
$secure=ConvertTo-SecureString (Get-Content -LiteralPath $Enrollment -Raw).Trim()
try{$e=([Net.NetworkCredential]::new('', $secure)).Password|ConvertFrom-Json}finally{$secure.Dispose()}
$login=Invoke-RestMethod ($c.address+'/v1/auth/approle/login') -Method Post -ContentType application/json -Body (Iis-Json @{role_id=$e.role_id;secret_id=$e.secret_id}) -NoProxy -TimeoutSec 20
$headers=@{'X-Vault-Token'=$login.auth.client_token}
$process=$null
try {
    $records=[ordered]@{}
    foreach($entry in Get-IisCredentialReferences $c){$records[$entry.Name]=(Invoke-RestMethod ($c.address+'/v1/'+$entry.Value) -Headers $headers -NoProxy -TimeoutSec 20).data.data}
    if($DeniedRecord -notmatch '^kv/data/[a-zA-Z0-9_/-]+$'){throw 'Invalid denied record probe.'}
    $denied=Invoke-WebRequest ($c.address+'/v1/'+$DeniedRecord) -Headers $headers -NoProxy -TimeoutSec 20 -SkipHttpErrorCheck
    if($denied.StatusCode -ne 403){throw 'Desktop workload unexpectedly has access to production identity.'}
    $c.directory=$WorkDirectory
    foreach($leaf in 'state','state/keys','state/logs','state/output','web/releases'){$null=New-Item -ItemType Directory -Path (Join-Path $WorkDirectory $leaf) -Force}
    Write-IisAtomic (Join-Path $WorkDirectory 'state/postgres-root.crt') $c.database.certificate
    Write-IisAtomic (Join-Path $WorkDirectory 'state/control.token') $records.chess.control_token
    Write-IisAtomic (Join-Path $WorkDirectory 'state/chess-client.json') (Iis-Json @{url=$c.chess.url;operators=$c.chess.operators;tokenFile=(Join-Path $WorkDirectory 'state/control.token')})
    $release=Join-Path $WorkDirectory ('web/releases/'+$manifest.revision)
    if(-not(Test-Path $release)){Copy-Item -LiteralPath $c.publish -Destination $release -Recurse}
    $null=Test-IisPackage $release
    Write-IisAtomic (Join-Path $WorkDirectory 'web/web.config') (Get-IisWebConfig $manifest.revision (Join-Path $WorkDirectory 'state/logs'))
    $express=Join-Path $env:ProgramFiles 'IIS Express'
    [xml]$xml=Get-Content (Join-Path $express 'config/templates/PersonalWebServer/applicationhost.config') -Raw
    $group=$xml.configuration.configSections.sectionGroup|Where-Object name -EQ 'system.webServer'
    $section=$xml.CreateElement('section');$section.SetAttribute('name','aspNetCore');$section.SetAttribute('overrideModeDefault','Allow');$null=$group.AppendChild($section)
    $module=$xml.CreateElement('add');$module.SetAttribute('name','AspNetCoreModuleV2');$module.SetAttribute('image',(Join-Path $express 'Asp.Net Core Module/V2/aspnetcorev2.dll'));$null=$xml.configuration.'system.webServer'.globalModules.AppendChild($module)
    $module=$xml.CreateElement('add');$module.SetAttribute('name','AspNetCoreModuleV2');$null=$xml.configuration.location.'system.webServer'.modules.AppendChild($module)
    $site=$xml.configuration.'system.applicationHost'.sites.site
    $site.SetAttribute('name','LaplacePackage');$site.application.virtualDirectory.SetAttribute('physicalPath',(Join-Path $WorkDirectory 'web'))
    $site.bindings.binding.SetAttribute('bindingInformation',(':'+ $Port +':localhost'))
    $site.application.SetAttribute('applicationPool','Clr4IntegratedAppPool')
    ($xml.configuration.'system.applicationHost'.applicationPools.add | Where-Object name -EQ 'Clr4IntegratedAppPool').SetAttribute('managedRuntimeVersion','')
    $config=Join-Path $WorkDirectory 'applicationhost.config';$xml.Save($config)
    $values=Get-IisEnvironment $c ([pscustomobject]$records)
    $values.LAPLACE_OUT=Join-Path $WorkDirectory 'state/output'
    $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $express 'iisexpress.exe'))
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    foreach($key in @($info.Environment.Keys|Where-Object {$_ -match '^(LAPLACE_|ASPNETCORE_|DOTNET_)'})){$null=$info.Environment.Remove($key)}
    foreach($key in $values.Keys){$info.Environment[$key]=$values[$key]}
    $info.ArgumentList.Add('/config:'+ $config);$info.ArgumentList.Add('/site:LaplacePackage');$info.ArgumentList.Add('/systray:false')
    $process=[Diagnostics.Process]::Start($info)
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    for($attempt=0;$attempt -lt 15;$attempt++){
        try{$result=Test-IisEndpoint $c -OperatorToken $records.api.operator_token -Base ('http://localhost:'+$Port);break}catch{if($process.HasExited -or $attempt -eq 14){throw};Start-Sleep -Seconds 1}
    }
    $result.package=$manifest.revision;$result.hosting='IIS Express / ANCM in-process';$result.workload_login='AppRole';$result.denied_record=$DeniedRecord
    Write-IisAtomic (Join-Path $WorkDirectory 'verification.json') (Iis-Json $result)
    $result|ConvertTo-Json
} finally {
    if($process){if(-not $process.HasExited){$process.Kill($true)};$process.WaitForExit();Write-IisAtomic (Join-Path $WorkDirectory 'iis.stdout.log') $stdout.GetAwaiter().GetResult();Write-IisAtomic (Join-Path $WorkDirectory 'iis.stderr.log') $stderr.GetAwaiter().GetResult();$process.Dispose()}
    $null=Invoke-RestMethod ($c.address+'/v1/auth/token/revoke-self') -Method Post -Headers $headers -NoProxy -TimeoutSec 20
}
