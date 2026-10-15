#Requires -Version 7
param(
    [Parameter(Mandatory = $true)][ValidateSet('acquire', 'release')][string]$Action,
    [Parameter(Mandatory = $true)][string]$Tree
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'laplace-paths.ps1')

$treePath = Resolve-LaplaceTreePath $Tree
$lockDir = Join-Path $treePath '.lap-lock'
$ownerFile = Join-Path $lockDir 'owner.json'

if ($Action -eq 'release') {
    Remove-Item -Recurse -Force $lockDir -ErrorAction SilentlyContinue
    exit 0
}

$ownerProc = (Get-Process -Id $PID).Parent
if (-not $ownerProc) { throw 'Cannot identify the calling build process.' }
$ownerPid = $ownerProc.Id
$ownerStart = ''
$ownerName = 'unknown'
if ($ownerProc) {
    try { $ownerStart = $ownerProc.StartTime.ToString('o') } catch {}
    $ownerName = $ownerProc.ProcessName
}

if (-not (Test-Path $treePath)) { New-Item -ItemType Directory -Force -Path $treePath | Out-Null }

$waitMax = 1800
if ($env:LAPLACE_LOCK_WAIT) { $waitMax = [int]$env:LAPLACE_LOCK_WAIT }
$elapsed = 0

while ($true) {
    try {
        New-Item -ItemType Directory -Path $lockDir -ErrorAction Stop | Out-Null
        @{ pid = $ownerPid; start = $ownerStart; name = $ownerName; acquired = (Get-Date).ToString('o') } |
            ConvertTo-Json -Compress | Set-Content -Path $ownerFile
        exit 0
    } catch {
        $cur = $null
        try { $cur = Get-Content $ownerFile -Raw -ErrorAction Stop | ConvertFrom-Json } catch {}
        $alive = $false
        if ($cur -and $cur.pid) {
            if ($cur.pid -eq $ownerPid) { exit 0 }
            $p = Get-Process -Id $cur.pid -ErrorAction SilentlyContinue
            if ($p) {
                $curStart = $null
                $recordedStart = $null
                try { $curStart = $p.StartTime.ToUniversalTime() } catch {}
                try { if ($cur.start) { $recordedStart = ([DateTimeOffset]$cur.start).UtcDateTime } } catch {}
                # JSON readers can return either a string or a DateTime. Compare
                # instants, and retain a live process's lock when its start is unreadable.
                if (-not $recordedStart -or -not $curStart -or $curStart -eq $recordedStart) { $alive = $true }
            }
        }
        if (-not $alive) {
            Write-Host "tree-lock: clearing stale lock on $Tree at $treePath (owner gone)"
            Remove-Item -Recurse -Force $lockDir -ErrorAction SilentlyContinue
            continue
        }
        if ($elapsed -eq 0) {
            Write-Host "tree-lock: $Tree is locked by PID $($cur.pid) ($($cur.name)) since $($cur.acquired) -- waiting up to ${waitMax}s."
            Write-Host "tree-lock: lock path: $treePath"
            Write-Host "tree-lock: do NOT start a parallel build tree. scripts\win\locks.cmd shows holders."
        }
        if ($elapsed -ge $waitMax) {
            Write-Host "tree-lock: TIMEOUT after ${waitMax}s; $Tree still locked by PID $($cur.pid)."
            Write-Host "tree-lock: investigate with scripts\win\locks.cmd -- do not fork a new build tree."
            exit 1
        }
        Start-Sleep -Seconds 5
        $elapsed += 5
    }
}
