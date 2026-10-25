#Requires -Version 7
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../deploy/windows/iis-runtime.ps1"
$work=Join-Path ([IO.Path]::GetTempPath()) ('iis-cache-closure-'+[Guid]::NewGuid().ToString('N'))
$files=@()
$names=@('tier0.bin','tier0.flags','tier0.flags.layout','highway.bin','laplace_chess_position_perfcache.bin','laplace_chess_transition_perfcache.bin')
foreach($component in @('','cli','uci')) {
    $share=Join-Path (Join-Path $work $component) 'share/laplace'
    $null=New-Item -ItemType Directory -Path $share -Force
    foreach($name in $names) {
        $path=Join-Path $share $name
        [IO.File]::WriteAllBytes($path,[byte[]]@(1,2,3,4))
        $files+=$path
    }
}
Test-IisCacheClosure $work
foreach($path in $files) {
    $bytes=[IO.File]::ReadAllBytes($path)
    Remove-Item -LiteralPath $path
    $caught=$false
    try {Test-IisCacheClosure $work} catch {
        if($_.Exception.Message -notlike 'Incomplete IIS cache:*'){throw}
        $caught=$true
    } finally {[IO.File]::WriteAllBytes($path,$bytes)}
    if(-not $caught){throw "Missing cache was accepted: $path"}
}
foreach($component in 'cli','uci') {
  foreach($name in $names) {
    $path=Join-Path $work "$component/share/laplace/$name"
    [IO.File]::WriteAllBytes($path,[byte[]]@(4,3,2,1))
    $caught=$false
    try {Test-IisCacheClosure $work} catch {
        if($_.Exception.Message -notlike 'IIS cache differs*'){throw}
        $caught=$true
    } finally {[IO.File]::WriteAllBytes($path,[byte[]]@(1,2,3,4))}
    if(-not $caught){throw "Mismatched cache was accepted: $component"}
  }
}
Test-IisCacheClosure $work
Write-Host 'PASS package closure requires matching Tier-0, highway and chess files for API, CLI and UCI; binary semantics are checked by the cache loader.'
