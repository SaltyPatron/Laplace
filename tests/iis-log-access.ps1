#Requires -Version 7
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../deploy/windows/iis-access.ps1"
$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
$work=Join-Path $temp ('iis-log-access-'+[Guid]::NewGuid().ToString('N'))
$logs=Join-Path $work 'logs'
$private=Join-Path $work 'private'
try {
    $null=New-Item -ItemType Directory -Path $logs,$private
    $before=(Get-Acl -LiteralPath $private).Sddl
    Grant-IisPostgresLogReader $logs 'S-1-5-20'
    Grant-IisPostgresLogReader $logs 'S-1-5-20'
    [IO.File]::WriteAllText((Join-Path $logs 'event.csv'),'actual log file')
    $rules=@((Get-Acl -LiteralPath $logs).GetAccessRules($true,$false,[Security.Principal.SecurityIdentifier]) |
        Where-Object {$_.IdentityReference.Value -eq 'S-1-5-20' -and $_.AccessControlType -eq 'Allow'})
    if($rules.Count -ne 1){throw 'Log reader permission is missing or duplicated.'}
    $read=[Security.AccessControl.FileSystemRights]::ReadAndExecute
    $write=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete
    if(($rules[0].FileSystemRights -band $read) -ne $read -or ($rules[0].FileSystemRights -band $write)){
        throw 'Database log reader needs read access without write/delete rights.'
    }
    $inherited=@((Get-Acl -LiteralPath (Join-Path $logs 'event.csv')).GetAccessRules($false,$true,[Security.Principal.SecurityIdentifier]) |
        Where-Object {$_.IdentityReference.Value -eq 'S-1-5-20'})
    if(-not $inherited.Count){throw 'New log files do not inherit database reader access.'}
    $artifact=Join-Path $work 'artifact.bin';$runtime=Join-Path $work 'runtime';$linked=Join-Path $runtime 'tier0.bin'
    [IO.File]::WriteAllText($artifact,'immutable Native floor fixture')
    $null=New-Item -ItemType Directory -Path $runtime
    Grant-IisRuntimeReaders $runtime @('S-1-5-20')
    $null=New-Item -ItemType HardLink -Path $linked -Target $artifact
    $hasReader={param($path) @((Get-Acl -LiteralPath $path).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) |
        Where-Object {$_.IdentityReference.Value -eq 'S-1-5-20' -and $_.AccessControlType -eq 'Allow' -and
            ($_.FileSystemRights -band $read) -eq $read}).Count -gt 0}
    if(& $hasReader $linked){throw 'Fixture did not retain its source inode ACL.'}
    Grant-IisRuntimeReaders $runtime @('S-1-5-20') -Recurse
    if(-not (& $hasReader $linked)){throw 'Published hard-linked Native floor is unreadable.'}
    $publishedAcl=(Get-Acl -LiteralPath $linked).Sddl
    Grant-IisRuntimeReaders $runtime @('S-1-5-20') -Recurse
    if((Get-Acl -LiteralPath $linked).Sddl -cne $publishedAcl){throw 'Runtime reader grants are not idempotent.'}
    if((Get-Acl -LiteralPath $private).Sddl -cne $before){throw 'Log reader setup changed an unrelated private directory.'}
    Write-Host 'PASS log reader ACL is read-only, inherited, idempotent and confined to the log directory.'
}finally{
    $resolved=[IO.Path]::GetFullPath($work)
    if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected temporary log test path.'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
