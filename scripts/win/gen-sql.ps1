param(
    [string]$Root = (Resolve-Path "$PSScriptRoot\..\.."),
    [string]$Version = "0.1.0",
    [string]$Stage = "",
    # Refresh mode: expand ONE substrate .sql.in module and apply it to a live
    # database (modules are CREATE OR REPLACE, so this is idempotent), e.g.
    #   gen-sql.ps1 -Module 26_generation.sql.in -Database laplace_code
    [string]$Module = "",
    [string]$Database = ""
)
$ErrorActionPreference = 'Stop'
if (-not $Stage) { $Stage = Join-Path $Root 'build-win-ext\stage' }
if (-not $Module) {
    New-Item -ItemType Directory -Force -Path "$Stage\lib", "$Stage\extension" | Out-Null
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

function Build-Extension {
    param([string]$Name, [bool]$StripExtschema)
    $srcDir = Join-Path $Root "extension\$Name\sql"

    $cfgDir = Join-Path $env:TEMP "laplace_sqlgen_$Name"
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    $defsIn = Get-Content -LiteralPath (Join-Path $srcDir 'sqldefines.h.in') -Encoding UTF8 -Raw
    [System.IO.File]::WriteAllText((Join-Path $cfgDir 'sqldefines.h'),
        ($defsIn -replace '@PROJECT_VERSION@', $Version), [System.Text.UTF8Encoding]::new($false))

    $macros = @{}
    $lines = [System.Collections.Generic.List[string]]::new()
    Expand-SqlIn -File (Join-Path $srcDir "$Name.sql.in") `
        -IncludeDirs @($cfgDir, $srcDir) -Macros $macros -Out $lines

    $sql = ($lines -join "`n")
    $sql = $sql -creplace 'MODULE_PATHNAME', $Name
    if ($StripExtschema) { $sql = $sql -replace '@extschema@\.', '' }
    $outSql = Join-Path "$Stage\extension" "$Name--$Version.sql"
    [System.IO.File]::WriteAllText($outSql, $sql + "`n", [System.Text.UTF8Encoding]::new($false))

    $ctl = Get-Content -LiteralPath (Join-Path $Root "extension\$Name\$Name.control.in") -Encoding UTF8 -Raw
    $ctl = $ctl -replace '@PROJECT_VERSION@', $Version
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
    $lines = [System.Collections.Generic.List[string]]::new()
    Expand-SqlIn -File (Join-Path $cfgDir 'sqldefines.h') -IncludeDirs @($cfgDir, $srcDir) -Macros $macros -Out $lines
    $lines.Clear()
    Expand-SqlIn -File (Join-Path $srcDir $Module) -IncludeDirs @($cfgDir, $srcDir) -Macros $macros -Out $lines

    $sql = ($lines -join "`n")
    $sql = $sql -creplace 'MODULE_PATHNAME', $Name
    $sql = $sql -creplace '@extschema@', 'laplace'
    # refresh mode reruns whole modules against live DBs: CREATE TABLE/INDEX must be
    # idempotent (functions already are via CREATE OR REPLACE)
    $sql = $sql -creplace 'CREATE (UNLOGGED )?TABLE (?!IF NOT EXISTS)', 'CREATE $1TABLE IF NOT EXISTS '
    $sql = $sql -creplace 'CREATE (UNIQUE )?INDEX (?!IF NOT EXISTS)', 'CREATE $1INDEX IF NOT EXISTS '
    $sql = "SET search_path = laplace, public;`nSET check_function_bodies = off;`n" + $sql

    $tmp = Join-Path $env:TEMP "laplace_refresh_$($Module -replace '\W','_').sql"
    [System.IO.File]::WriteAllText($tmp, $sql + "`n", [System.Text.UTF8Encoding]::new($false))

    if (-not $env:PGPASSWORD) { $env:PGPASSWORD = 'postgres' }
    $psql = if ($env:PGBIN) { Join-Path $env:PGBIN 'psql.exe' } else { 'C:\Program Files\PostgreSQL\18\bin\psql.exe' }
    & $psql -h localhost -U postgres -d $Database `
        -v ON_ERROR_STOP=1 -f $tmp
    if ($LASTEXITCODE -ne 0) { throw "refresh failed: $Module → $Database" }
    Write-Host "refreshed: $Module → $Database"
    exit 0
}

Build-Extension -Name 'laplace_geom' -StripExtschema $true
Build-Extension -Name 'laplace_substrate' -StripExtschema $false
