#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Published,[Parameter(Mandatory)][string]$Destination,[Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{7,64}$')][string]$Revision)
. "$PSScriptRoot/iis-runtime.ps1"
if(-not(Test-Path -LiteralPath (Join-Path $Published 'uci/laplace-uci.exe') -PathType Leaf)){throw 'The application publication must include its UCI runtime. Use Build-IisPackage.ps1.'}
foreach($component in @('','cli','uci')) {
    & "$PSScriptRoot/Test-NativeSqlCatalog.ps1" -Library (Join-Path (Join-Path $Published $component) 'laplace_core.dll')
}
if(Test-Path -LiteralPath $Destination){throw 'Choose a new package directory; existing packages are immutable.'}
Test-IisCacheClosure $Published
$null=New-Item -ItemType Directory -Path $Destination
$files=@(Get-ChildItem -LiteralPath $Published -File -Recurse | Where-Object Name -NotIn @('web.config','iis-package.json','verify-application-release.py') | ForEach-Object {
    $relative=[IO.Path]::GetRelativePath($Published,$_.FullName)
    $target=Join-Path $Destination $relative
    $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
    Copy-Item -LiteralPath $_.FullName -Destination $target
    @{path=$relative;sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$identity=(@($files|Sort-Object path|ForEach-Object {$_.path+':'+$_.sha256}) -join "`n")
$digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).ToLowerInvariant()
Write-IisAtomic (Join-Path $Destination 'iis-package.json') (Iis-Json @{revision=$digest;source_revision=$Revision;created_utc=[DateTime]::UtcNow.ToString('o');components=@('api','cli','uci');files=$files})
$null=Test-IisPackage $Destination
Write-Host "Packaged $($files.Count) verified files at $Destination"
