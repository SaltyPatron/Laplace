# Code readers retain access across publication; PostgreSQL reads only declared
# log directories. Credentials and other private state use neither reader policy.
$ErrorActionPreference='Stop'
function Grant-IisPostgresLogReader([string]$Path,[string]$Account) {
    if([string]::IsNullOrWhiteSpace($Account)){throw 'Declare LAPLACE_PG_SERVICE_ACCOUNT before configuring application log access.'}
    $sid=if($Account -in @('LocalSystem','NT AUTHORITY\SYSTEM')){
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    }elseif($Account -match '^S-1-'){[Security.Principal.SecurityIdentifier]::new($Account)}else{
        ([Security.Principal.NTAccount]::new($Account)).Translate([Security.Principal.SecurityIdentifier])
    }
    $null=New-Item -ItemType Directory -Path $Path -Force
    $acl=Get-Acl -LiteralPath $Path
    $rights=[Security.AccessControl.FileSystemRights]::ReadAndExecute
    $inheritance=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
        if($rule.IdentityReference.Value -eq $sid.Value -and $rule.AccessControlType -eq 'Allow' -and
            ($rule.FileSystemRights -band $rights) -eq $rights -and
            ($rule.InheritanceFlags -band $inheritance) -eq $inheritance -and
            $rule.PropagationFlags -eq 'None'){return}
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,$rights,$inheritance,'None','Allow'))
    Set-IisFileAccess $Path $acl
}
function Set-IisFileAccess([string]$Path,$Acl) {
    $access=[Security.AccessControl.AccessControlSections]::Access
    if(Test-Path -LiteralPath $Path -PathType Container) {
        $security=[Security.AccessControl.DirectorySecurity]::new()
        $security.SetSecurityDescriptorSddlForm($Acl.GetSecurityDescriptorSddlForm($access),$access)
        [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($Path),$security)
    }else{
        $security=[Security.AccessControl.FileSecurity]::new()
        $security.SetSecurityDescriptorSddlForm($Acl.GetSecurityDescriptorSddlForm($access),$access)
        [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($Path),$security)
    }
}
function Get-IisRuntimeReaders($Config) {
    $readers=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($name in @($Config.public_runtime_readers)) {
        if(-not $name){continue}
        $sid=if($name -match '^S-1-') {[Security.Principal.SecurityIdentifier]::new($name)}else{
            ([Security.Principal.NTAccount]::new($name)).Translate([Security.Principal.SecurityIdentifier])
        }
        $null=$readers.Add($sid.Value)
    }
    $web=Join-Path $Config.directory 'web'
    $selection=Join-Path $web 'active.json'
    $paths=@($web,$selection)
    if(Test-Path -LiteralPath $selection -PathType Leaf) {
        $active=[IO.File]::ReadAllText($selection)|ConvertFrom-Json
        if($active.revision -notmatch '^[a-f0-9]{7,64}$'){throw 'Invalid installed release selection.'}
        $paths+=Join-Path $web ('releases/'+$active.revision)
    }
    $readOnly=[Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::Synchronize
    foreach($path in $paths) {
        if(-not(Test-Path -LiteralPath $path)){continue}
        foreach($rule in (Get-Acl -LiteralPath $path).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            # Preserve the read-only pool grants enrolled by companion installers.
            # Neither a general user grant nor any write/administrative grant is imported.
            if($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -match '^S-1-5-82-' -and
               ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadAndExecute) -eq [Security.AccessControl.FileSystemRights]::ReadAndExecute -and
               ($rule.FileSystemRights -band (-bnot [int]$readOnly)) -eq 0) {
                $null=$readers.Add($rule.IdentityReference.Value)
            }
        }
    }
    @($readers|Sort-Object)
}
function Grant-IisRuntimeReaders([string]$Path,[string[]]$Readers) {
    if(-not $Readers){return}
    $acl=Get-Acl -LiteralPath $Path
    $inheritance=if(Test-Path -LiteralPath $Path -PathType Container){'ContainerInherit,ObjectInherit'}else{'None'}
    $flags=[Security.AccessControl.InheritanceFlags]$inheritance
    $existing=@($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    $changed=$false
    foreach($reader in $Readers) {
        if(@($existing|Where-Object {
            $_.IdentityReference.Value -eq $reader -and $_.AccessControlType -eq 'Allow' -and
            ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadAndExecute) -eq [Security.AccessControl.FileSystemRights]::ReadAndExecute -and
            ($_.InheritanceFlags -band $flags) -eq $flags -and $_.PropagationFlags -eq 'None'
        }).Count){continue}
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($reader),'ReadAndExecute',$inheritance,'None','Allow'))
        $changed=$true
    }
    if($changed){Set-IisFileAccess $Path $acl}
}
function Write-IisPublicAtomic([string]$Path,[string]$Content,[string[]]$Readers) {
    $temporary=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.new'
    try {
        [IO.File]::WriteAllText($temporary,$Content)
        if(Test-Path -LiteralPath $Path) {
            $access=[Security.AccessControl.AccessControlSections]::Access
            $acl=Get-Acl -LiteralPath $temporary
            $acl.SetSecurityDescriptorSddlForm((Get-Acl -LiteralPath $Path).GetSecurityDescriptorSddlForm($access),$access)
            Set-IisFileAccess $temporary $acl
        }
        Grant-IisRuntimeReaders $temporary $Readers
        [IO.File]::Move($temporary,$Path,$true)
    } finally { if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary} }
}
