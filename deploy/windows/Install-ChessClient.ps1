#requires -Version 7
[CmdletBinding()]
param(
    [ValidateSet('Install','Start','Render')][string]$Operation='Install',
    [Parameter(Mandatory)][string]$Configuration,
    [string]$Enrollment
)
$ErrorActionPreference='Stop'
$c=Get-Content -LiteralPath $Configuration -Raw | ConvertFrom-Json
$root=[IO.Path]::GetFullPath($c.directory)
if (-not [IO.Path]::IsPathFullyQualified($c.directory)) { throw 'Declare an absolute client installation directory.' }
# Persist an explicit executable so startup never selects an unrelated PATH version.
if ($Operation -ne 'Render') {
    $bao = if ($c.PSObject.Properties['bao'] -and $c.bao) { $c.bao } else { (Get-Command bao).Source }
    $baoVersion = (& $bao version | Out-String).Trim()
    if ($LASTEXITCODE -or $baoVersion -notmatch '^OpenBao v(\d+\.\d+\.\d+)\s' -or [version]$Matches[1] -lt [version]'2.7.1') {
        throw 'Declare bao as an OpenBao 2.7.1-or-newer executable; older renewal retry behavior is unsafe.'
    }
    $c | Add-Member -NotePropertyName bao -NotePropertyValue $bao -Force
}
function Write-Atomic([string]$Path,[string]$Content) {
    $temporary=$Path+'.new'
    [IO.File]::WriteAllText($temporary,$Content)
    [IO.File]::Move($temporary,$Path,$true)
}
if ($Operation -eq 'Render') {
    $record=Get-Content -LiteralPath (Join-Path $root 'candidate.json') -Raw | ConvertFrom-Json
    if ($record.control_token -notmatch '^[a-f0-9]{64}$') { throw 'The workload record has no valid controller credential.' }
    Write-Atomic (Join-Path $root 'control.token') $record.control_token
    foreach($entry in $c.secret_files.PSObject.Properties) {
        if ([IO.Path]::GetFileName($entry.Value) -ne $entry.Value) { throw 'Secret destinations must remain in the private installation directory.' }
        $value=$record.($entry.Name)
        if ($value -isnot [string] -or -not $value -or $value.Contains([char]0)) { throw "Missing workload field: $($entry.Name)" }
        Write-Atomic (Join-Path $root $entry.Value) $value
    }
    Write-Atomic (Join-Path $root 'render-receipt.json') (@{source='openbao-agent';rendered_utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json)
    return
}
[void](New-Item -ItemType Directory -Path $root -Force)
$acl=Get-Acl -LiteralPath $root
$acl.SetAccessRuleProtection($true,$false)
foreach($rule in @($acl.Access)) { [void]$acl.RemoveAccessRuleSpecific($rule) }
foreach($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
}
[IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($root),$acl)
$installed=Join-Path $root 'installation.json'
$script=Join-Path $root 'Install-ChessClient.ps1'
if ($Operation -eq 'Install') {
    if ($Enrollment) {
        $e=Get-Content -LiteralPath $Enrollment -Raw | ConvertFrom-Json
        if ($e.host -ne $env:COMPUTERNAME.ToLowerInvariant() -or $e.address -ne $c.address -or $e.record -ne $c.record) { throw 'Enrollment does not match the declared desktop workload.' }
        $secure=ConvertTo-SecureString ($e | ConvertTo-Json -Compress) -AsPlainText -Force
        try { Write-Atomic (Join-Path $root 'enrollment.dpapi') (ConvertFrom-SecureString $secure) } finally { $secure.Dispose() }
    }
    Write-Atomic $installed ($c | ConvertTo-Json -Depth 8)
    if ($PSCommandPath -ne $script) { Copy-Item -LiteralPath $PSCommandPath -Destination $script -Force }
    $client=Join-Path $root 'api-client.json'
    Write-Atomic $client (@{url=$c.url;tokenFile=(Join-Path $root 'control.token');operators=$c.operators} | ConvertTo-Json -Depth 5)
    foreach($entry in $c.public_files.PSObject.Properties) {
        if ([IO.Path]::GetFileName($entry.Name) -ne $entry.Name) { throw 'Public configuration must remain in the installation directory.' }
        Write-Atomic (Join-Path $root $entry.Name) $(if($entry.Value -is [string]){$entry.Value}else{$entry.Value | ConvertTo-Json -Depth 12})
    }
    if ($c.connector) {
        $bin=[IO.Path]::GetFullPath($c.connector.bin)
        $release=Join-Path $bin $c.connector.revision
        if ($c.connector.revision -notmatch '^[a-f0-9]{7,64}$' -or -not (Test-Path (Join-Path $c.connector.publish 'laplace-uci-connect.exe'))) { throw 'Declare a published UCI connector revision.' }
        [void](New-Item -ItemType Directory -Path $release -Force)
        Copy-Item -Path (Join-Path $c.connector.publish '*') -Destination $release -Recurse -Force
        $profilePath=Join-Path $root 'uci-client.json'
        Write-Atomic (Join-Path $bin 'laplace-uci-connect.cmd') ('@"'+(Join-Path $release 'laplace-uci-connect.exe')+'" %* --configuration "'+$profilePath+'"'+"`r`n")
        $userPath=[Environment]::GetEnvironmentVariable('Path','User')
        if ($bin -notin ($userPath -split ';')) { [Environment]::SetEnvironmentVariable('Path',($userPath.TrimEnd(';')+';'+$bin),'User') }
    }
    [Environment]::SetEnvironmentVariable('LAPLACE_LICHESS_CONTROL_CLIENT',$client,'User')
    $shell=New-Object -ComObject WScript.Shell
    $shortcut=$shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Startup')) 'Laplace mono chess credentials.lnk'))
    $shortcut.TargetPath=(Get-Command pwsh).Source
    $shortcut.Arguments='-NoProfile -WindowStyle Hidden -File "'+$script+'" -Operation Start -Configuration "'+$installed+'"'
    $shortcut.WindowStyle=7
    $shortcut.Save()
}
$state=Join-Path $root 'agent-process.json'
$active=$false
if (Test-Path -LiteralPath $state) {
    $previous=Get-Content -LiteralPath $state -Raw | ConvertFrom-Json
    $process=Get-Process -Id $previous.pid -ErrorAction SilentlyContinue
    $active=$process -and $process.Path -eq $previous.executable -and $process.StartTime.ToUniversalTime() -eq ([DateTimeOffset]$previous.started_utc).UtcDateTime
    if ($active -and $previous.executable -ine $bao) {
        Stop-Process -Id $process.Id
        $process.WaitForExit()
        $active=$false
    }
}
if (-not $active) {
    $secure=ConvertTo-SecureString (Get-Content -LiteralPath (Join-Path $root 'enrollment.dpapi') -Raw).Trim()
    try { $e=([Net.NetworkCredential]::new('', $secure)).Password | ConvertFrom-Json } finally { $secure.Dispose() }
    Write-Atomic (Join-Path $root 'role-id') $e.role_id
    Write-Atomic (Join-Path $root 'secret-id') $e.secret_id
    $record=$c.record
    $template='{{ with secret "'+$record+'" }}{{ .Data.data | toJSON }}{{ end }}'
    function Json($Value) { ConvertTo-Json -InputObject $Value -Compress }
    $agent=@"
vault { address = $(Json $c.address) }
auto_auth { method "approle" { config = {
  role_id_file_path = $(Json (Join-Path $root 'role-id'))
  secret_id_file_path = $(Json (Join-Path $root 'secret-id'))
  remove_secret_id_file_after_reading = true
} } }
template_config { static_secret_render_interval = "1m"
exit_on_retry_failure = true }
template {
  contents = $(Json $template)
  destination = $(Json (Join-Path $root 'candidate.json'))
  perms = "0600"
  backup = false
  error_on_missing_key = true
  exec { command = $(Json @((Get-Command pwsh).Source,'-NoProfile','-File',$script,'-Operation','Render','-Configuration',$installed))
  timeout = "30s" }
}
"@
    Write-Atomic (Join-Path $root 'agent.hcl') $agent
    Remove-Item -LiteralPath (Join-Path $root 'candidate.json') -Force -ErrorAction SilentlyContinue
    $began=[DateTime]::UtcNow
    $process=Start-Process -FilePath $bao -ArgumentList @('agent',('-config="'+(Join-Path $root 'agent.hcl')+'"')) -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'agent.stdout.log') -RedirectStandardError (Join-Path $root 'agent.stderr.log')
    Write-Atomic $state (@{pid=$process.Id;executable=$bao;started_utc=$process.StartTime.ToUniversalTime().ToString('o')} | ConvertTo-Json)
    $rendered=$false
    for($i=0;$i -lt 60;$i++) {
        $receipt=Join-Path $root 'render-receipt.json'
        if ((Test-Path $receipt) -and ([DateTimeOffset](Get-Content $receipt -Raw | ConvertFrom-Json).rendered_utc).UtcDateTime -ge $began) { $rendered=$true;break }
        if ($process.HasExited) { throw "OpenBao Agent exited; inspect $root/agent.stderr.log" }
        Start-Sleep -Seconds 1
    }
    if (-not $rendered) { throw "The desktop workload has not rendered its credential; inspect $root/agent.stderr.log" }
}
$token=(Get-Content -LiteralPath (Join-Path $root 'control.token') -Raw).Trim()
foreach($entry in $c.secret_files.PSObject.Properties) {
    $ready=$false
    for($i=0;$i -lt 65;$i++) {
        if(Test-Path (Join-Path $root $entry.Value)){$ready=$true;break}
        Start-Sleep -Seconds 1
    }
    if(-not $ready){throw "The workload agent has not rendered $($entry.Name)."}
}
$status=Invoke-RestMethod ($c.url.TrimEnd('/')+'/status') -Headers @{Authorization='Bearer '+$token} -TimeoutSec 10
if ($Enrollment) { Remove-Item -LiteralPath $Enrollment -Force }
Write-Host "Desktop chess control installed: $($status.controller), selection $($status.selection.profile ?? 'Off'). OpenBao uses the DPAPI-enrolled workload; startup is registered for this Windows user."
