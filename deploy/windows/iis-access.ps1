# Public runtime consumers retain read access across setup and publication.
# Credential/state directories never use this policy.
$ErrorActionPreference='Stop'
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
    foreach($reader in $Readers) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($reader),'ReadAndExecute',$inheritance,'None','Allow'))
    }
    Set-IisFileAccess $Path $acl
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
