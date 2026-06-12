# Who is holding Laplace artifacts (the LNK1104 / copy-fail explainer).
# Usage: locks.ps1 [-Kill]
#   -Kill terminates the SAFE holders (Laplace.Cli, endpoint/test dotnet hosts, engine test exes).
#   Never kills postgres (deploy DLLs are released by install-extensions.cmd hot-swap / --recycle)
#   and never kills live build tools (ninja/cmake/icx -- those mean a build is in progress: wait).
param([switch]$Kill)
$ErrorActionPreference = 'SilentlyContinue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$deployLib = 'D:\Data\Postgres\laplace\lib'
$buildTools = @('ninja', 'cmake', 'icx', 'icx-cc', 'clang', 'link')

$holders = @()

# Pass 1: processes whose main image lives under the repo.
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

# Pass 2: dotnet/testhost processes with repo DLLs loaded (dotnet run hosts, test runners).
foreach ($p in Get-Process -Name dotnet, testhost) {
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

# Pass 3: deployed extension DLLs pinned by postgres backends (exclusive-open probe).
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

if (-not $holders) {
    Write-Host 'locks: nothing is holding Laplace artifacts.'
    exit 0
}

$holders | Sort-Object SafeToKill -Descending | Format-Table -AutoSize | Out-String -Width 240 | Write-Host

if ($Kill) {
    $targets = $holders | Where-Object { $_.SafeToKill -and $_.Pid -ne '-' }
    if (-not $targets) { Write-Host 'locks: no safe-to-kill holders.'; exit 0 }
    foreach ($t in $targets | Sort-Object Pid -Unique) {
        Write-Host "locks: stopping PID $($t.Pid) ($($t.Process))"
        Stop-Process -Id $t.Pid -Force
    }
    Write-Host 'locks: done. Re-run locks.cmd to confirm.'
}
