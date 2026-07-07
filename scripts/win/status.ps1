


$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'laplace-paths.ps1')
$Root = Get-LaplaceRepoRoot
$deployRoot = if ($env:LAPLACE_DEPLOY) { $env:LAPLACE_DEPLOY } else { Join-Path (Get-LaplaceDataRoot) 'deploy' }
$engineBuild = Resolve-LaplaceTreePath 'build-win'
$extBuild = Resolve-LaplaceTreePath 'build-win-ext'
$asanBuild = Resolve-LaplaceTreePath 'build-win-asan'
$deployLib = Join-Path $deployRoot 'lib'
$deployShare = Join-Path $deployRoot 'share'
$deployPg = ($deployRoot -replace '\\', '/')
$psql = if (Get-Command psql) { 'psql' } else { 'C:\Program Files\PostgreSQL\18\bin\psql.exe' }
$env:PGPASSWORD = if ($env:PGPASSWORD) { $env:PGPASSWORD } else { 'postgres' }
$env:PGCONNECT_TIMEOUT = '3'

function Section($t) { Write-Host "`n=== $t ===" }

Section 'GIT'
Push-Location $Root
$branch = git rev-parse --abbrev-ref HEAD
$dirty = (git status --porcelain --ignore-submodules | Measure-Object).Count
$last = git log -1 --format='%h %ad %s' --date=format:'%m-%d %H:%M'
Write-Host "branch $branch, $dirty dirty file(s), last: $last"
Pop-Location

Section 'BUILD TREES (ninja dry-run; pending=0 means current)'
foreach ($pair in @(
        @{ label = 'build-win'; path = $engineBuild; hint = '' },
        @{ label = 'build-win-ext'; path = $extBuild; hint = 's/extensions' },
        @{ label = 'build-win-asan'; path = $asanBuild; hint = '-asan' }
    )) {
    $tree = $pair.label
    $treePath = $pair.path
    if (-not (Test-Path (Join-Path $treePath 'build.ninja'))) {
        Write-Host ("{0,-16} NOT CONFIGURED (build-engine{1}.cmd configures it)" -f $tree, $pair.hint)
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

Section "DEPLOY ($deployRoot vs build outputs)"
$pairs = @(
    @{ src = Join-Path $extBuild 'laplace_geom\laplace_geom.dll'; dst = Join-Path $deployLib 'laplace_geom.dll'; label = 'laplace_geom.dll' },
    @{ src = Join-Path $extBuild 'laplace_substrate\laplace_substrate.dll'; dst = Join-Path $deployLib 'laplace_substrate.dll'; label = 'laplace_substrate.dll' },
    @{ src = Join-Path $engineBuild 'core\laplace_core.dll'; dst = Join-Path $deployLib 'laplace_core.dll'; label = 'laplace_core.dll' },
    @{ src = Join-Path $engineBuild 'dynamics\laplace_dynamics.dll'; dst = Join-Path $deployLib 'laplace_dynamics.dll'; label = 'laplace_dynamics.dll' },
    @{ src = Join-Path $engineBuild 'core\perfcache\laplace_t0_perfcache.bin'; dst = Join-Path $deployShare 'laplace_t0_perfcache.bin'; label = 'laplace_t0_perfcache.bin' },
    @{ src = Join-Path $engineBuild 'core\perfcache\laplace_highway_perfcache.bin'; dst = Join-Path $deployShare 'laplace_highway_perfcache.bin'; label = 'laplace_highway_perfcache.bin' }
)
foreach ($pair in $pairs) {
    $s = $pair.src
    $d = $pair.dst
    if (-not (Test-Path $s)) { Write-Host ("{0,-32} NOT BUILT" -f $pair.label); continue }
    if (-not (Test-Path $d)) { Write-Host ("{0,-32} NOT DEPLOYED (install-extensions.cmd)" -f $pair.label); continue }
    
    
    $h1 = (Get-FileHash $s -Algorithm SHA256).Hash
    $h2 = (Get-FileHash $d -Algorithm SHA256).Hash
    if (-not $h1 -or -not $h2) { Write-Host ("{0,-32} HASH FAILED (PSModulePath pollution? see env.cmd)" -f $pair.label); continue }
    $same = $h1 -eq $h2
    Write-Host ("{0,-32} {1}" -f $pair.label, $(if ($same) { 'deployed = built' } else { 'STALE DEPLOY (install-extensions.cmd)' }))
}
$stale = Get-ChildItem (Join-Path $deployLib '*.stale~*') 2>$null
if ($stale) { Write-Host ("{0} hot-swap leftover(s) pending cleanup (next install-extensions run)" -f $stale.Count) }

Section 'POSTGRES'
$svc = Get-Service postgresql-x64-18
Write-Host "service postgresql-x64-18: $($svc.Status)"
if ($svc.Status -eq 'Running') {
    $gucT0 = (& $psql -h localhost -U postgres -d postgres -tAc "SHOW laplace_substrate.perfcache_path;" 2>&1 | Where-Object { $_ })
    $gucHw = (& $psql -h localhost -U postgres -d postgres -tAc "SHOW laplace_substrate.highway_perfcache_path;" 2>&1 | Where-Object { $_ })
    $wantT0 = "$deployPg/share/laplace_t0_perfcache.bin"
    $wantHw = "$deployPg/share/laplace_highway_perfcache.bin"
    Write-Host "perfcache_path GUC: $(if ($gucT0 -eq $wantT0) { 'OK' } else { "STALE ($gucT0)" })"
    Write-Host "highway_perfcache_path GUC: $(if ($gucHw -eq $wantHw) { 'OK' } else { "STALE ($gucHw)" })"
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
