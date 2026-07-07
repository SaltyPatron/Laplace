# Shared Laplace path law for PowerShell scripts (matches scripts/win/env.cmd).
$ErrorActionPreference = 'Stop'

function Import-LaplaceEnv {
    if ($env:LAPLACE_ENV_LOADED -eq '1') { return }
    $envCmd = Join-Path $PSScriptRoot 'env.cmd'
    if (-not (Test-Path $envCmd)) {
        throw "laplace-paths: missing $envCmd"
    }
    $lines = cmd /c "`"$envCmd`" >nul 2>&1 && set LAPLACE && set INGEST && set REPOS && set PGPASSWORD"
    foreach ($line in $lines) {
        $eq = $line.IndexOf('=')
        if ($eq -le 0) { continue }
        $name = $line.Substring(0, $eq)
        $value = $line.Substring($eq + 1)
        Set-Item -Path "Env:$name" -Value $value
    }
}

function Get-LaplaceRepoRoot {
    Import-LaplaceEnv
    if ($env:LAPLACE_ROOT) {
        return (Resolve-Path $env:LAPLACE_ROOT).Path
    }
    return (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

function Get-LaplaceDataRoot {
    Import-LaplaceEnv
    if ($env:LAPLACE_DATA_ROOT) { return $env:LAPLACE_DATA_ROOT }
    return 'D:\Data\Laplace'
}

function Get-LaplaceBuildRoot {
    Import-LaplaceEnv
    if ($env:LAPLACE_BUILD_ROOT) { return $env:LAPLACE_BUILD_ROOT }
    return Get-LaplaceDataRoot
}

function Resolve-LaplaceTreePath {
    param([Parameter(Mandatory = $true)][string]$Name)
    Import-LaplaceEnv
    $fromEnv = @{
        'build-win'         = $env:LAPLACE_ENGINE_BUILD
        'build-win-ext'     = $env:LAPLACE_EXT_BUILD
        'build-win-asan'    = $env:LAPLACE_ENGINE_BUILD_ASAN
        'build-cutechess'   = $env:LAPLACE_CUTECHESS_BUILD
    }
    if ($fromEnv.ContainsKey($Name) -and -not [string]::IsNullOrWhiteSpace($fromEnv[$Name])) {
        return $fromEnv[$Name]
    }
    $dataRoot = Get-LaplaceDataRoot
    $defaults = @{
        'build-win'         = Join-Path $dataRoot 'build-win'
        'build-win-ext'     = Join-Path $dataRoot 'build-win-ext'
        'build-win-asan'    = Join-Path $dataRoot 'build-win-asan'
        'build-cutechess'   = Join-Path $dataRoot 'build-cutechess'
    }
    if ($defaults.ContainsKey($Name)) { return $defaults[$Name] }
    return Join-Path (Get-LaplaceRepoRoot) $Name
}

function Get-LaplaceBuildTreePaths {
    @(
        @{ label = 'build-win'; path = (Resolve-LaplaceTreePath 'build-win') },
        @{ label = 'build-win-ext'; path = (Resolve-LaplaceTreePath 'build-win-ext') },
        @{ label = 'build-win-asan'; path = (Resolve-LaplaceTreePath 'build-win-asan') },
        @{ label = 'build-cutechess'; path = (Resolve-LaplaceTreePath 'build-cutechess') }
    )
}

function Remove-StaleInRepoBuildArtifacts {
    param([string]$RepoRoot = (Get-LaplaceRepoRoot))
    foreach ($name in @('build-win', 'build-win-ext', 'build-win-asan', 'build-cutechess', 'out')) {
        $stale = Join-Path $RepoRoot $name
        if (Test-Path $stale) {
            Write-Host "laplace-paths: removing stale in-repo $name ..."
            Remove-Item -Recurse -Force $stale -ErrorAction SilentlyContinue
        }
    }
}

function Get-LaplaceTreeLocks {
    $locks = @()
    foreach ($pair in Get-LaplaceBuildTreePaths) {
        $lockFile = Join-Path $pair.path '.lap-lock\owner.json'
        if (-not (Test-Path $lockFile)) { continue }
        try {
            $o = Get-Content $lockFile -Raw | ConvertFrom-Json
            $alive = $false
            if ($o.pid) {
                $p = Get-Process -Id $o.pid -ErrorAction SilentlyContinue
                if ($p) {
                    $curStart = ''
                    try { $curStart = $p.StartTime.ToString('o') } catch {}
                    if (-not $o.start -or $curStart -eq $o.start) { $alive = $true }
                }
            }
            $locks += [pscustomobject]@{
                Tree = $pair.label
                Path = $pair.path
                Pid = $o.pid
                Name = $o.name
                Acquired = $o.acquired
                Alive = $alive
            }
        } catch {}
    }
    return $locks
}
