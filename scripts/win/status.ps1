


$ErrorActionPreference = 'SilentlyContinue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$deployLib = 'D:\Data\Postgres\laplace\lib'
$psql = if (Get-Command psql) { 'psql' } else { 'C:\Program Files\PostgreSQL\18\bin\psql.exe' }
$env:PGPASSWORD = if ($env:PGPASSWORD) { $env:PGPASSWORD } else { 'postgres' }
$env:PGCONNECT_TIMEOUT = '3'

function Section($t) { Write-Host "`n=== $t ===" }

Section 'GIT'
Push-Location $root
$branch = git rev-parse --abbrev-ref HEAD
$dirty = (git status --porcelain --ignore-submodules | Measure-Object).Count
$last = git log -1 --format='%h %ad %s' --date=format:'%m-%d %H:%M'
Write-Host "branch $branch, $dirty dirty file(s), last: $last"
Pop-Location

Section 'BUILD TREES (ninja dry-run; pending=0 means current)'
foreach ($tree in 'build-win', 'build-win-ext', 'build-win-asan') {
    $treePath = Join-Path $root $tree
    if (-not (Test-Path (Join-Path $treePath 'build.ninja'))) {
        Write-Host ("{0,-16} NOT CONFIGURED (build-engine{1}.cmd configures it)" -f $tree,
            $(if ($tree -eq 'build-win-ext') { 's/extensions' } elseif ($tree -eq 'build-win-asan') { '-asan' } else { '' }))
        continue
    }
    $lock = Join-Path $treePath '.lap-lock\owner.json'
    $lockNote = ''
    if (Test-Path $lock) {
        $o = Get-Content $lock -Raw | ConvertFrom-Json
        $lockNote = " [BUILD IN PROGRESS: PID $($o.pid) since $($o.acquired)]"
    }
    $out = & ninja -C $treePath -n 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("{0,-16} DRY-RUN FAILED: {1}$lockNote" -f $tree, ($out | Select-Object -First 1))
    } elseif ($out -match 'no work to do') {
        Write-Host ("{0,-16} up to date$lockNote" -f $tree)
    } else {
        $pending = ($out | Where-Object { $_ -match '^\[' } | Measure-Object).Count
        Write-Host ("{0,-16} {1} pending build step(s)$lockNote" -f $tree, $pending)
    }
}

Section 'DEPLOY (D:\Data\Postgres\laplace vs build outputs)'
$pairs = @(
    @{ src = 'build-win-ext\laplace_geom\laplace_geom.dll'; dst = 'laplace_geom.dll' },
    @{ src = 'build-win-ext\laplace_substrate\laplace_substrate.dll'; dst = 'laplace_substrate.dll' },
    @{ src = 'build-win\core\laplace_core.dll'; dst = 'laplace_core.dll' },
    @{ src = 'build-win\dynamics\laplace_dynamics.dll'; dst = 'laplace_dynamics.dll' }
)
foreach ($pair in $pairs) {
    $s = Join-Path $root $pair.src
    $d = Join-Path $deployLib $pair.dst
    if (-not (Test-Path $s)) { Write-Host ("{0,-26} NOT BUILT" -f $pair.dst); continue }
    if (-not (Test-Path $d)) { Write-Host ("{0,-26} NOT DEPLOYED (install-extensions.cmd)" -f $pair.dst); continue }
    
    
    $h1 = (Get-FileHash $s -Algorithm SHA256).Hash
    $h2 = (Get-FileHash $d -Algorithm SHA256).Hash
    if (-not $h1 -or -not $h2) { Write-Host ("{0,-26} HASH FAILED (PSModulePath pollution? see env.cmd)" -f $pair.dst); continue }
    $same = $h1 -eq $h2
    Write-Host ("{0,-26} {1}" -f $pair.dst, $(if ($same) { 'deployed = built' } else { 'STALE DEPLOY (install-extensions.cmd)' }))
}
$stale = Get-ChildItem (Join-Path $deployLib '*.stale~*') 2>$null
if ($stale) { Write-Host ("{0} hot-swap leftover(s) pending cleanup (next install-extensions run)" -f $stale.Count) }

Section 'POSTGRES'
$svc = Get-Service postgresql-x64-18
Write-Host "service postgresql-x64-18: $($svc.Status)"
if ($svc.Status -eq 'Running') {
    $ext = & $psql -h localhost -U postgres -d postgres -tAc "SELECT name || ' ' || default_version FROM pg_available_extensions WHERE name LIKE 'laplace%' ORDER BY 1;" 2>&1
    Write-Host "available extensions: $(($ext | Where-Object { $_ }) -join ', ')"
    $dbs = & $psql -h localhost -U postgres -d postgres -tAc "SELECT datname FROM pg_database WHERE datname LIKE 'laplace%' ORDER BY 1;" 2>&1
    Write-Host "databases: $(($dbs | Where-Object { $_ }) -join ', ')"
    if ($dbs -contains 'laplace') {
        $counts = & $psql -h localhost -U postgres -d laplace -tAc "SELECT c.relname || '=' || to_char(c.reltuples::bigint, 'FM999,999,999,999') FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'laplace' AND c.relname IN ('entities','physicalities','attestations','consensus') ORDER BY 1;" 2>&1
        Write-Host "row estimates: $(($counts | Where-Object { $_ }) -join ', ')"
        $ver = & $psql -h localhost -U postgres -d laplace -tAc "SELECT extversion FROM pg_extension WHERE extname = 'laplace_substrate';" 2>&1
        Write-Host "laplace db substrate version: $ver"
    }
}

Section 'ENDPOINT (serve.cmd port 5187)'
$listener = Get-NetTCPConnection -LocalPort 5187 -State Listen 2>$null
if ($listener) {
    $owner = (Get-Process -Id $listener[0].OwningProcess).ProcessName
    Write-Host "LISTENING (PID $($listener[0].OwningProcess), $owner) -- app builds will hit locks; locks.cmd --kill clears it"
} else {
    Write-Host 'not running'
}

Section 'LOCKS'
& (Join-Path $PSScriptRoot 'locks.ps1')
