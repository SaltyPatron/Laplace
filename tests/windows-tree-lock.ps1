#Requires -Version 7
$ErrorActionPreference='Stop'
$work=Join-Path ([IO.Path]::GetTempPath()) ('laplace-tree-lock-'+[Guid]::NewGuid().ToString('N'))
$tree=Join-Path $work 'build-win'
$lock=Join-Path $tree '.lap-lock'
$script=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../scripts/win/tree-lock.ps1'))
$pwsh=(Get-Process -Id $PID).Path
$names=@('LAPLACE_ENV_LOADED','LAPLACE_ENGINE_BUILD','LAPLACE_LOCK_WAIT')
$saved=@{}
foreach($name in $names){$saved[$name]=[Environment]::GetEnvironmentVariable($name)}
try {
    $env:LAPLACE_ENV_LOADED='1';$env:LAPLACE_ENGINE_BUILD=$tree;$env:LAPLACE_LOCK_WAIT='0'
    & $pwsh -NoProfile -File $script acquire build-win
    if($LASTEXITCODE){throw 'Lock acquisition failed.'}
    $file=Join-Path $lock 'owner.json'
    $before=[IO.File]::ReadAllText($file)
    $owner=$before | ConvertFrom-Json -DateKind String
    if($owner.pid -ne $PID -or $owner.start -cne (Get-Process -Id $PID).StartTime.ToString('o')){throw 'Lock does not identify the live calling process.'}
    & $pwsh -NoProfile -File $script acquire build-win
    if($LASTEXITCODE -or [IO.File]::ReadAllText($file) -cne $before){throw 'Repeated acquisition changed the owner.'}
    Write-Host 'PASS parent process identity and repeated build-lock acquisition without WMI.'

    $competitor=Join-Path $work 'competitor.ps1'
    Set-Content -LiteralPath $competitor -Value @'
param([string]$LockScript)
& (Get-Process -Id $PID).Path -NoProfile -File $LockScript acquire build-win
exit $LASTEXITCODE
'@
    & $pwsh -NoProfile -File $competitor $script
    if($LASTEXITCODE -ne 1 -or [IO.File]::ReadAllText($file) -cne $before){throw 'A competing build acquired or changed a live lock.'}
    Write-Host 'PASS competing build times out without changing the live lock.'

    & $pwsh -NoProfile -File $script release build-win
    if($LASTEXITCODE -or (Test-Path -LiteralPath $lock)){throw 'Lock release failed.'}
    Write-Host 'PASS build lock is released after completion.'
} finally {
    foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
    $resolved=[IO.Path]::GetFullPath($work)
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected test directory outside temporary storage.'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
exit 0
