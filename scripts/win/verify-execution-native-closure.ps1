[CmdletBinding()]
param([Parameter(Mandatory)][string]$Module)
$ErrorActionPreference = 'Stop'
$modulePath = (Resolve-Path -LiteralPath $Module).Path
$dumpbin = (Get-Command dumpbin.exe -ErrorAction Stop).Source
$imports = & $dumpbin /nologo /imports $modulePath
if ($LASTEXITCODE -ne 0) { throw "Cannot inspect the query module imports: $modulePath" }
$hostImports = $false
$borrowed = @()
foreach ($line in $imports) {
    if ($line -match '^\s{4}(\S+\.dll)\s*$') {
        $hostImports = $Matches[1] -ieq 'laplace_substrate.dll'
    }
    if ($hostImports -and $line -match '^\s+[0-9A-Fa-f]+\s+((?:lp_|tier_tree_|native_text_|text_identity_)\w+|laplace_substrate_\w*perfcache_path)\s*$') {
        $borrowed += $Matches[1]
    }
}
if ($borrowed.Count) {
    throw ('Versioned query module imports Native code or cache-path helpers from the preloaded host: ' + ($borrowed -join ', '))
}
Write-Output 'Execution Native closure is module-local; host imports do not pin Native text or tree code.'
