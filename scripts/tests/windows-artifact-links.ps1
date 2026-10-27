$ErrorActionPreference='Stop'
. "$PSScriptRoot/../../deploy/windows/iis-runtime.ps1"
$root=Join-Path ([IO.Path]::GetTempPath()) ('laplace-artifact-links-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
try {
    $source=Join-Path $root 'source.dll';$old=Join-Path $root 'old/runtime.dll';$fresh=Join-Path $root 'fresh/runtime.dll'
    [IO.File]::WriteAllText($source,'first runtime')
    Copy-IisArtifactFile $source $old
    Copy-IisArtifactFile $old $fresh
    $replacement=Join-Path $root 'replacement.dll'
    [IO.File]::WriteAllText($replacement,'second runtime')
    Copy-IisArtifactFile $replacement $fresh
    if([IO.File]::ReadAllText($old) -ne 'first runtime' -or [IO.File]::ReadAllText($source) -ne 'first runtime'){
        throw 'Replacing a package file modified a previous immutable artifact.'
    }
    if([IO.File]::ReadAllText($fresh) -ne 'second runtime'){throw 'The selected artifact was not replaced.'}
    $tree=Join-Path $root 'tree';$dest=Join-Path $root 'dest'
    $null=New-Item -ItemType Directory -Path (Join-Path $tree 'wwwroot/assets') -Force
    Set-Content -LiteralPath (Join-Path $tree 'api.dll') -Value api
    Set-Content -LiteralPath (Join-Path $tree 'wwwroot/assets/old.js') -Value old
    Copy-IisArtifactTree $tree $dest @('wwwroot')
    if(-not(Test-Path -LiteralPath (Join-Path $dest 'api.dll')) -or (Test-Path -LiteralPath (Join-Path $dest 'wwwroot'))){throw 'Component composition copied an excluded frontend subtree.'}
    'Immutable artifact replacement preserves old packages and excludes separately owned web assets.'
}finally{
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -ne [IO.Path]::GetTempPath().TrimEnd('\','/')){throw 'Invalid test cleanup root.'}
    Remove-Item -LiteralPath $root -Recurse -Force
}
