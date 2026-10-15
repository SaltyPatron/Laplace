#Requires -Version 7
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../deploy/windows/iis-runtime.ps1"
$work=Join-Path ([IO.Path]::GetTempPath()) ('iis-cache-closure-'+[Guid]::NewGuid().ToString('N'))
$files=@()
foreach($component in @('','cli','uci')) {
    $share=Join-Path (Join-Path $work $component) 'share/laplace'
    $null=New-Item -ItemType Directory -Path $share -Force
    foreach($name in 'position','transition') {
        $path=Join-Path $share "laplace_chess_${name}_perfcache.bin"
        [IO.File]::WriteAllBytes($path,[byte[]]@(1,2,3,4))
        $files+=$path
    }
}
Test-IisChessCacheClosure $work
foreach($path in $files) {
    $bytes=[IO.File]::ReadAllBytes($path)
    Remove-Item -LiteralPath $path
    $caught=$false
    try {Test-IisChessCacheClosure $work} catch {
        if($_.Exception.Message -notlike 'Incomplete IIS chess cache:*'){throw}
        $caught=$true
    } finally {[IO.File]::WriteAllBytes($path,$bytes)}
    if(-not $caught){throw "Missing cache was accepted: $path"}
}
foreach($component in 'cli','uci') {
    $path=Join-Path $work "$component/share/laplace/laplace_chess_transition_perfcache.bin"
    [IO.File]::WriteAllBytes($path,[byte[]]@(4,3,2,1))
    $caught=$false
    try {Test-IisChessCacheClosure $work} catch {
        if($_.Exception.Message -notlike 'IIS chess cache differs*'){throw}
        $caught=$true
    } finally {[IO.File]::WriteAllBytes($path,[byte[]]@(1,2,3,4))}
    if(-not $caught){throw "Mismatched cache was accepted: $component"}
}
Test-IisChessCacheClosure $work
Write-Host 'PASS package closure requires matching position and transition files for API, CLI and UCI; binary semantics are checked by the cache loader.'
