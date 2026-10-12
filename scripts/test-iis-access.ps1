#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Work)
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'This check requires actual Windows file ACLs.'}
. "$PSScriptRoot/../deploy/windows/iis-access.ps1"
$root=Join-Path ([IO.Path]::GetFullPath($Work)) ('iis-access-'+[Guid]::NewGuid().ToString('N'))
$web=Join-Path $root 'web'
$first=Join-Path $web 'releases/aaaaaaaa'
$second=Join-Path $web 'releases/bbbbbbbb'
$null=New-Item -ItemType Directory -Path $first,$second
$reader='S-1-5-82-1-2-3-4-5'
$writer='S-1-5-82-5-4-3-2-1'
function Assert-ReadOnly([string]$Path) {
    $rules=@((Get-Acl -LiteralPath $Path).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])|Where-Object {$_.IdentityReference.Value -eq $reader})
    if($rules.Count -eq 0){throw "Consumer read permission lost: $Path"}
    foreach($rule in $rules){if($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write){throw 'Read permission became write permission.'}}
}
$selection=Join-Path $web 'active.json'
[IO.File]::WriteAllText($selection,'{"revision":"aaaaaaaa"}')
Grant-IisRuntimeReaders $selection @($reader)
Grant-IisRuntimeReaders $first @($reader)
$acl=Get-Acl $first
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($writer),'Modify','ContainerInherit,ObjectInherit','None','Allow'))
Set-IisFileAccess $first $acl
$readers=@(Get-IisRuntimeReaders ([pscustomobject]@{directory=$root}))
if($readers -notcontains $reader -or $readers -contains $writer){throw 'Consumer detection lost read access or imported a write grant.'}
Grant-IisRuntimeReaders $second $readers
[IO.File]::WriteAllText((Join-Path $second 'engine.exe'),'permission probe')
Assert-ReadOnly (Join-Path $second 'engine.exe')
$before=(Get-Acl $second).Sddl
Grant-IisRuntimeReaders $second $readers
if((Get-Acl $second).Sddl -ne $before){throw 'Repeated publication changed an already granted ACL.'}
foreach($revision in 'bbbbbbbb','aaaaaaaa','bbbbbbbb'){
    Write-IisPublicAtomic $selection ('{"revision":"'+$revision+'"}') $readers
    Assert-ReadOnly $selection
    if(([IO.File]::ReadAllText($selection)|ConvertFrom-Json).revision -ne $revision){throw 'Publication did not select the requested revision.'}
}
# Repeated setup rebuilds the public directory policy, then restores enrolled readers.
$retained=@(Get-IisRuntimeReaders ([pscustomobject]@{directory=$root}))
$acl=Get-Acl $web
$acl.SetAccessRuleProtection($true,$true)
Set-IisFileAccess $web $acl
Grant-IisRuntimeReaders $web $retained
$third=Join-Path $web 'releases/cccccccc'
$null=New-Item -ItemType Directory -Path $third
[IO.File]::WriteAllText((Join-Path $third 'engine.exe'),'next permission probe')
Assert-ReadOnly (Join-Path $third 'engine.exe')
"Windows publication and setup retained read-only consumer access: $root"
