param([switch]$Kill)
$ErrorActionPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'laplace-paths.ps1')

$root = Get-LaplaceRepoRoot
$deployLib = if ($env:LAPLACE_DEPLOY) { Join-Path $env:LAPLACE_DEPLOY 'lib' } else { Join-Path (Get-LaplaceDataRoot) 'deploy\lib' }
$buildTools = @('ninja', 'cmake', 'icx', 'icx-cc', 'clang', 'link')

$holders = @()

foreach ($p in Get-Process) {
    $path = $null
    try { $path = $p.MainModule.FileName } catch { continue }
    if (-not $path) { continue }
    if ($path.StartsWith($root, 'OrdinalIgnoreCase')) {
        $isBuildTool = $buildTools -contains $p.ProcessName.ToLower()
        $holders += [pscustomobject]@{
            Pid = $p.Id; Process = $p.ProcessName; Holds = $path
            SafeToKill = -not $isBuildTool
            Note = if ($isBuildTool) { 'build in progress - wait, do not kill' } else { 'runs from repo' }
        }
    }
}

foreach ($pair in Get-LaplaceBuildTreePaths) {
    $treePath = $pair.path
    if (-not $treePath) { continue }
    foreach ($p in Get-Process) {
        $path = $null
        try { $path = $p.MainModule.FileName } catch { continue }
        if (-not $path) { continue }
        if ($path.StartsWith($treePath, 'OrdinalIgnoreCase')) {
            if ($holders.Pid -contains $p.Id) { continue }
            $isBuildTool = $buildTools -contains $p.ProcessName.ToLower()
            $holders += [pscustomobject]@{
                Pid = $p.Id; Process = $p.ProcessName; Holds = $path
                SafeToKill = -not $isBuildTool
                Note = if ($isBuildTool) { "build in progress ($($pair.label))" } else { "runs from $($pair.label)" }
            }
        }
    }
}

foreach ($p in Get-Process -Name dotnet, testhost -ErrorAction SilentlyContinue) {
    if ($holders.Pid -contains $p.Id) { continue }
    $hit = $null
    try {
        $hit = ($p.Modules | Where-Object {
                $_.FileName -and ($_.FileName.StartsWith($root, 'OrdinalIgnoreCase')) } | Select-Object -First 1).FileName
    } catch {}
    if ($hit) {
        $holders += [pscustomobject]@{
            Pid = $p.Id; Process = $p.ProcessName; Holds = $hit
            SafeToKill = $true; Note = 'managed host with repo assemblies loaded'
        }
    }
}

if (Test-Path $deployLib) {
    foreach ($f in Get-ChildItem (Join-Path $deployLib '*.dll')) {
        $locked = $false
        try {
            $s = [IO.File]::Open($f.FullName, 'Open', 'ReadWrite', 'None'); $s.Dispose()
        } catch { $locked = $true }
        if ($locked) {
            $holders += [pscustomobject]@{
                Pid = '-'; Process = 'postgres'; Holds = $f.FullName
                SafeToKill = $false; Note = 'mapped by PG backends; install-extensions.cmd hot-swaps it'
            }
        }
    }
}

$treeLocks = Get-LaplaceTreeLocks
if ($treeLocks) {
    Write-Host 'tree locks:'
    $treeLocks | Format-Table Tree, Path, Pid, Name, Acquired, Alive -AutoSize | Out-String -Width 240 | Write-Host
}

if (-not $holders -and -not $treeLocks) {
    Write-Host 'locks: nothing is holding Laplace artifacts.'
    exit 0
}

if ($holders) {
    Write-Host 'process / file locks:'
    $holders | Sort-Object SafeToKill -Descending | Format-Table -AutoSize | Out-String -Width 240 | Write-Host
}

if ($Kill) {
    $targets = $holders | Where-Object { $_.SafeToKill -and $_.Pid -ne '-' }
    if (-not $targets) { Write-Host 'locks: no safe-to-kill holders.'; exit 0 }
    foreach ($t in $targets | Sort-Object Pid -Unique) {
        Write-Host "locks: stopping PID $($t.Pid) ($($t.Process))"
        Stop-Process -Id $t.Pid -Force
    }
    Write-Host 'locks: done. Re-run locks.cmd to confirm.'
}
