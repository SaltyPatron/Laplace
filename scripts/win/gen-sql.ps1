param(
    [string]$Root = (Resolve-Path "$PSScriptRoot\..\.."),
    [string]$Version = "0.1.0",
    [string]$Stage = ""
)
$ErrorActionPreference = 'Stop'
if (-not $Stage) { $Stage = Join-Path $Root 'build-win-ext\stage' }
New-Item -ItemType Directory -Force -Path "$Stage\lib", "$Stage\extension" | Out-Null

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

Build-Extension -Name 'laplace_geom' -StripExtschema $true
Build-Extension -Name 'laplace_substrate' -StripExtschema $false
