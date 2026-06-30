param(
    [string]$Root = (Resolve-Path "$PSScriptRoot\..\.."),
    [string]$Version = "0.1.0",
    [string]$Stage = "",
    [string]$Module = "",
    [string]$Database = ""
)
$ErrorActionPreference = 'Stop'
if (-not $Stage) { $Stage = Join-Path $Root 'build-win-ext\stage' }
if (-not $Module) {
    New-Item -ItemType Directory -Force -Path "$Stage\lib", "$Stage\extension" | Out-Null
}

function Read-Manifest {
    param([string]$Path)
    Get-Content -LiteralPath $Path -Encoding UTF8 |
        Where-Object { $_ -and -not ($_.TrimStart().StartsWith('#')) }
}

function Expand-SqlIn {
    param([string]$File, [string[]]$IncludeDirs, [hashtable]$Macros, [System.Collections.Generic.List[string]]$Out)
    foreach ($line in (Get-Content -LiteralPath $File -Encoding UTF8)) {
        if ($line -match '^\s*#include\s+"([^"]+)"') {
            $inc = $Matches[1]
            $resolved = $null
            foreach ($d in $IncludeDirs) {
                $cand = Join-Path $d $inc
                if (Test-Path -LiteralPath $cand) { $resolved = $cand; break }
            }
            if (-not $resolved) { throw "include not found: $inc (from $File)" }
            Expand-SqlIn -File $resolved -IncludeDirs $IncludeDirs -Macros $Macros -Out $Out
        }
        elseif ($line -match '^\s*#define\s+(\w+)\s+(.*)$') {
            $Macros[$Matches[1]] = $Matches[2].TrimEnd()
        }
        elseif ($line -match '^\s*#') {
        }
        else {
            $expanded = $line
            foreach ($name in ($Macros.Keys | Sort-Object Length -Descending)) {
                $expanded = $expanded -creplace "\b$([regex]::Escape($name))\b", $Macros[$name].Replace('$', '$$')
            }
            $Out.Add($expanded)
        }
    }
}

function Expand-Manifest {
    param(
        [string]$ManifestPath,
        [string]$SrcDir,
        [string[]]$IncludeDirs,
        [hashtable]$Macros
    )
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($rel in (Read-Manifest $ManifestPath)) {
        $file = Join-Path $SrcDir $rel
        if (-not (Test-Path -LiteralPath $file)) { throw "manifest entry not found: $rel" }
        Expand-SqlIn -File $file -IncludeDirs $IncludeDirs -Macros $Macros -Out $lines
    }
    return $lines
}

function Build-Extension {
    param([string]$Name, [bool]$StripExtschema, [string]$ManifestName = '')
    $srcDir = Join-Path $Root "extension\$Name\sql"

    $cfgDir = Join-Path $env:TEMP "laplace_sqlgen_$Name"
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    $defsIn = Get-Content -LiteralPath (Join-Path $srcDir 'sqldefines.h.in') -Encoding UTF8 -Raw
    [System.IO.File]::WriteAllText((Join-Path $cfgDir 'sqldefines.h'),
        ($defsIn -replace '@PROJECT_VERSION@', $Version), [System.Text.UTF8Encoding]::new($false))

    $macros = @{}
    $manifestPath = if ($ManifestName) { Join-Path $srcDir $ManifestName } else { $null }
    if ($manifestPath -and (Test-Path -LiteralPath $manifestPath)) {
        $lines = [System.Collections.Generic.List[string]]::new()
        Expand-SqlIn -File (Join-Path $cfgDir 'sqldefines.h') `
            -IncludeDirs @($cfgDir, $srcDir) -Macros $macros -Out $lines
        $lines.Clear()
        $lines = Expand-Manifest -ManifestPath $manifestPath -SrcDir $srcDir `
            -IncludeDirs @($cfgDir, $srcDir) -Macros $macros
    }
    else {
        $lines = [System.Collections.Generic.List[string]]::new()
        Expand-SqlIn -File (Join-Path $srcDir "$Name.sql.in") `
            -IncludeDirs @($cfgDir, $srcDir) -Macros $macros -Out $lines
    }

    $sql = ($lines -join "`n")
    $sql = $sql -creplace 'MODULE_PATHNAME', $Name
    if ($StripExtschema) { $sql = $sql -replace '@extschema@\.', '' }
    $outSql = Join-Path "$Stage\extension" "$Name--$Version.sql"
    [System.IO.File]::WriteAllText($outSql, $sql + "`n", [System.Text.UTF8Encoding]::new($false))

    $ctl = Get-Content -LiteralPath (Join-Path $Root "extension\$Name\$Name.control.in") -Encoding UTF8 -Raw
    $ctl = $ctl -replace '@PROJECT_VERSION@', $Version
    $ctl = $ctl -replace '@EXT_VERSION@', $Version
    $ctl = $ctl -replace "\`$libdir/$Name", $Name
    $outCtl = Join-Path "$Stage\extension" "$Name.control"
    [System.IO.File]::WriteAllText($outCtl, $ctl, [System.Text.UTF8Encoding]::new($false))
    Write-Host "generated: $outSql"
    Write-Host "generated: $outCtl"
}

if ($Module) {
    if (-not $Database) { throw "-Module requires -Database" }
    $Name   = 'laplace_substrate'
    $srcDir = Join-Path $Root "extension\$Name\sql"

    $cfgDir = Join-Path $env:TEMP "laplace_sqlgen_$Name"
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    $defsIn = Get-Content -LiteralPath (Join-Path $srcDir 'sqldefines.h.in') -Encoding UTF8 -Raw
    [System.IO.File]::WriteAllText((Join-Path $cfgDir 'sqldefines.h'),
        ($defsIn -replace '@PROJECT_VERSION@', $Version), [System.Text.UTF8Encoding]::new($false))

    $macros = @{}
    $modulePath = if ([System.IO.Path]::IsPathRooted($Module)) { $Module } else { Join-Path $srcDir $Module }
    if (-not (Test-Path -LiteralPath $modulePath)) { throw "module not found: $Module" }

    $lines = [System.Collections.Generic.List[string]]::new()
    Expand-SqlIn -File (Join-Path $cfgDir 'sqldefines.h') -IncludeDirs @($cfgDir, $srcDir) -Macros $macros -Out $lines
    $lines.Clear()
    Expand-SqlIn -File $modulePath -IncludeDirs @($cfgDir, $srcDir) -Macros $macros -Out $lines

    $sql = ($lines -join "`n")
    $sql = $sql -creplace 'MODULE_PATHNAME', $Name
    $sql = $sql -creplace '@extschema@', 'laplace'
    $sql = $sql -creplace 'CREATE (UNLOGGED )?TABLE (?!IF NOT EXISTS)', 'CREATE $1TABLE IF NOT EXISTS '
    $sql = $sql -creplace 'CREATE (UNIQUE )?INDEX (?!IF NOT EXISTS)', 'CREATE $1INDEX IF NOT EXISTS '
    $sql = $sql -creplace '(?m)^\s*SELECT pg_extension_config_dump.*$', ''
    $sql = "SET search_path = laplace, public;`nSET check_function_bodies = off;`n" + $sql

    $tmp = Join-Path $env:TEMP "laplace_refresh_$($Module -replace '[\\/]','_').sql"
    [System.IO.File]::WriteAllText($tmp, $sql + "`n", [System.Text.UTF8Encoding]::new($false))

    if (-not $env:PGPASSWORD) { $env:PGPASSWORD = 'postgres' }
    $psql = if ($env:PGBIN) { Join-Path $env:PGBIN 'psql.exe' } else { 'C:\Program Files\PostgreSQL\18\bin\psql.exe' }
    & $psql -h localhost -U postgres -d $Database -v ON_ERROR_STOP=1 -f $tmp
    if ($LASTEXITCODE -ne 0) { throw "refresh failed: $Module → $Database" }
    Write-Host "refreshed: $Module → $Database"
    exit 0
}

function Test-Manifest {
    param([string]$SqlDir, [string]$ManifestName)
    $manifestPath = Join-Path $SqlDir $ManifestName
    $seen = @{}
    $errors = [System.Collections.Generic.List[string]]::new()
    foreach ($rel in (Read-Manifest $manifestPath)) {
        if ($seen.ContainsKey($rel)) { $errors.Add("$ManifestName duplicate: $rel") }
        $seen[$rel] = $true
        $file = Join-Path $SqlDir $rel
        if (-not (Test-Path -LiteralPath $file)) { $errors.Add("$ManifestName missing: $rel") }
    }
    foreach ($numbered in (Get-ChildItem -LiteralPath $SqlDir -File -Filter '[0-9][0-9]_*.sql.in' -ErrorAction SilentlyContinue)) {
        $errors.Add("numbered bundle remains: $($numbered.Name)")
    }
    if ($errors.Count) { throw ($errors -join "`n") }
    return $seen.Count
}

function Assert-UpgradeManifest {
    param([string]$SqlDir)
    $skip = @('bootstrap/bootstrap.sql.in', 'seed/canonical_names_seed.sql.in')
    $install = Read-Manifest (Join-Path $SqlDir 'manifest.install')
    $upgrade = Read-Manifest (Join-Path $SqlDir 'manifest.upgrade')
    $expected = $install | Where-Object { $skip -notcontains $_ }
    $missing = $expected | Where-Object { $_ -notin $upgrade }
    $extra = $upgrade | Where-Object { $_ -notin $expected }
    if ($missing -or $extra) {
        throw "manifest.upgrade out of sync with manifest.install`nmissing: $($missing -join ', ')`nextra: $($extra -join ', ')"
    }
}

function Get-SqlObjectNames {
    param([string]$Path)
    $pat = '(?i)CREATE\s+(?:OR\s+REPLACE\s+)?(?:UNIQUE\s+)?(?:UNLOGGED\s+)?(TABLE|VIEW|INDEX|FUNCTION|AGGREGATE|PROCEDURE)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:CONCURRENTLY\s+)?(?:ONLY\s+)?(?:(?:@extschema@|laplace)\.)?("?)([A-Za-z_][A-Za-z0-9_]*)'
    $names = @{}
    foreach ($m in [regex]::Matches((Get-Content -LiteralPath $Path -Raw), $pat)) {
        $k = $m.Groups[3].Value.ToLower()
        if ($names.ContainsKey($k)) { $names[$k]++ } else { $names[$k] = 1 }
    }
    return $names
}

function Build-HeadBaseline {
    param([string]$SqlDir, [string]$OutPath)
    $cfgDir = Join-Path $Root 'build-win-ext\_sqlgen_head_baseline'
    if (Test-Path $cfgDir) { Remove-Item -Recurse -Force $cfgDir }
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    $defsIn = Get-Content -LiteralPath (Join-Path $SqlDir 'sqldefines.h.in') -Raw
    [System.IO.File]::WriteAllText((Join-Path $cfgDir 'sqldefines.h'),
        ($defsIn -replace '@PROJECT_VERSION@', $Version), [System.Text.UTF8Encoding]::new($false))
    $macros = @{}
    $out = [System.Collections.Generic.List[string]]::new()
    function Expand-HeadFile([string]$File) {
        foreach ($line in (Get-Content -LiteralPath $File -Encoding UTF8)) {
            if ($line -match '^\s*#include\s+"([^"]+)"') {
                $inc = $Matches[1]
                $cand = Join-Path $cfgDir $inc
                if (-not (Test-Path $cand)) { $cand = Join-Path $SqlDir $inc }
                if (-not (Test-Path $cand)) {
                    $gitPath = "extension/laplace_substrate/sql/$inc"
                    $content = git -C $Root show "HEAD:$gitPath" 2>$null
                    if ($LASTEXITCODE -ne 0) { throw "HEAD baseline missing: $gitPath" }
                    [System.IO.File]::WriteAllText($cand, $content, [System.Text.UTF8Encoding]::new($false))
                }
                Expand-HeadFile $cand
            }
            elseif ($line -match '^\s*#define\s+(\w+)\s+(.*)$') { $macros[$Matches[1]] = $Matches[2].TrimEnd() }
            elseif ($line -match '^\s*#') { }
            else {
                $expanded = $line
                foreach ($name in ($macros.Keys | Sort-Object Length -Descending)) {
                    $expanded = $expanded -creplace "\b$([regex]::Escape($name))\b", $macros[$name].Replace('$', '$$')
                }
                $out.Add($expanded)
            }
        }
    }
    $chainPath = Join-Path $cfgDir 'chain.sql.in'
    git -C $Root show HEAD:extension/laplace_substrate/sql/laplace_substrate.sql.in | Set-Content -Encoding UTF8 $chainPath
    Expand-HeadFile $chainPath
    [System.IO.File]::WriteAllText($OutPath, ($out -join "`n"), [System.Text.UTF8Encoding]::new($false))
}

function Assert-ObjectParity {
    param([string]$NewSql, [string]$BaselineSql, [string[]]$AllowedMissing)
    $head = Get-SqlObjectNames $BaselineSql
    $new = Get-SqlObjectNames $NewSql
    $missing = [System.Collections.Generic.List[string]]::new()
    foreach ($k in $head.Keys) {
        if ($AllowedMissing -contains $k) { continue }
        if (-not $new.ContainsKey($k)) { $missing.Add($k) }
        elseif ($new[$k] -lt $head[$k]) { $missing.Add("${k} head=$($head[$k]) new=$($new[$k])") }
    }
    if ($missing.Count) { throw "object parity failed vs git HEAD:`n$($missing -join "`n")" }
    Write-Host "parity OK: $($new.Count) unique names, $($new.Values | Measure-Object -Sum | Select-Object -ExpandProperty Sum) CREATE vs HEAD $($head.Count)/$($head.Values | Measure-Object -Sum | Select-Object -ExpandProperty Sum)"
}

$substrateSql = Join-Path $Root 'extension\laplace_substrate\sql'
$geomSql = Join-Path $Root 'extension\laplace_geom\sql'
$installCount = Test-Manifest -SqlDir $substrateSql -ManifestName 'manifest.install'
Test-Manifest -SqlDir $substrateSql -ManifestName 'manifest.upgrade' | Out-Null
Test-Manifest -SqlDir $geomSql -ManifestName 'manifest.install' | Out-Null
Assert-UpgradeManifest -SqlDir $substrateSql
Write-Host "manifest.install entries: $installCount"

Build-Extension -Name 'laplace_geom' -StripExtschema $true -ManifestName 'manifest.install'
Build-Extension -Name 'laplace_substrate' -StripExtschema $false -ManifestName 'manifest.install'

# Upgrade body for ALTER EXTENSION ... UPDATE
$srcDir = Join-Path $Root 'extension\laplace_substrate\sql'
$cfgDir = Join-Path $env:TEMP 'laplace_sqlgen_laplace_substrate_upgrade'
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
$defsIn = Get-Content -LiteralPath (Join-Path $srcDir 'sqldefines.h.in') -Encoding UTF8 -Raw
[System.IO.File]::WriteAllText((Join-Path $cfgDir 'sqldefines.h'),
    ($defsIn -replace '@PROJECT_VERSION@', $Version), [System.Text.UTF8Encoding]::new($false))
$macros = @{}
$upLines = Expand-Manifest -ManifestPath (Join-Path $srcDir 'manifest.upgrade') -SrcDir $srcDir `
    -IncludeDirs @($cfgDir, $srcDir) -Macros $macros
$upSql = ($upLines -join "`n")
$upSql = $upSql -creplace 'MODULE_PATHNAME', 'laplace_substrate'
$upOut = Join-Path "$Stage\extension" "laplace_substrate_upgrade.sql"
[System.IO.File]::WriteAllText($upOut, $upSql + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "generated: $upOut"

$baseline = Join-Path $Root 'build-win-ext\baseline_HEAD.sql'
Build-HeadBaseline -SqlDir $substrateSql -OutPath $baseline
Assert-ObjectParity -NewSql (Join-Path $Stage 'extension\laplace_substrate--0.1.0.sql') `
    -BaselineSql $baseline -AllowedMissing @('content_descent_novel_ordinals')
