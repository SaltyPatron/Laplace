#Requires -Version 7
# apply-declared-access.ps1 -Database NAME [-Plan]
# A database db-reset recreated has none of the access its runtime role is declared to have
# (Laplace-Operations development/installation.json: substrate_database -> substrate_role,
# application_database -> application_role), so a reset left it unusable by the very roles that
# seed and serve it (Laplace#1879). This applies that declaration through the Operations step that
# owns it, development/configure-windows.ps1 (roles, CONNECT, schema grants and default privileges,
# the development SSPI rules, with a backup of pg_hba/pg_ident), then checks the substrate role can
# use schema laplace and MAINTAIN (so ANALYZE) every substrate table and partition.
# A database no declaration names is said so and left as it is.
# The declaration: LAPLACE_ACCESS_DECLARATION, else <sources>\Laplace-Operations\development\
# installation.json, sources being LAPLACE_SRC, REPOS, or the directory beside this checkout.
# -Plan prints what would run and runs nothing.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Database, [switch]$Plan)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$decl = $env:LAPLACE_ACCESS_DECLARATION
if (-not $decl) {
    $src = if ($env:LAPLACE_SRC) { $env:LAPLACE_SRC } elseif ($env:REPOS) { $env:REPOS } else { Split-Path -Parent $root }
    $decl = Join-Path $src 'Laplace-Operations\development\installation.json'
}
if (-not (Test-Path -LiteralPath $decl)) { "declared access: no declaration at $decl; the roles of $Database are not configured"; exit 0 }
$c = Get-Content -LiteralPath $decl -Raw | ConvertFrom-Json
$kind = if ($c.substrate_database -eq $Database) { 'substrate' } elseif ($c.application_database -eq $Database) { 'application' } else { $null }
if (-not $kind) { "declared access: $decl names no database $Database (substrate $($c.substrate_database), application $($c.application_database)); nothing applied"; exit 0 }
$configure = Join-Path (Split-Path -Parent $decl) 'configure-windows.ps1'
if (-not (Test-Path -LiteralPath $configure)) { throw "declared access: $configure is missing beside the declaration" }
$role = if ($kind -eq 'substrate') { $c.substrate_role } else { $c.application_role }
"declared access: $Database is the $kind database of $decl; its role $role"
if ($Plan) { "plan: pwsh -NoProfile -File $configure -Installation $decl"; exit 0 }
& pwsh -NoProfile -File $configure -Installation $decl
if ($LASTEXITCODE) { throw "declared access: configure-windows.ps1 exited $LASTEXITCODE" }
if ($kind -eq 'substrate') {
    $psql = if ($env:PGBIN) { Join-Path $env:PGBIN 'psql.exe' } else { 'psql' }
    $ok = & $psql -X -h localhost -U postgres -d $Database -Atq -v ON_ERROR_STOP=1 -c "SELECT has_schema_privilege('$role', 'laplace', 'USAGE') AND has_database_privilege('$role', current_database(), 'CONNECT')"
    if ($LASTEXITCODE -or $ok -ne 't') { throw "declared access: $role cannot use schema laplace of $Database after configuration" }
    "declared access: $role may connect to $Database and use schema laplace"
    # The role seeds, so it refreshes the statistics of what it writes: ANALYZE skips, with only a
    # WARNING, every table or partition it may not MAINTAIN, and the site then reads the seeded
    # database as empty (Laplace#1879). Every substrate table and partition is checked.
    $denied = & $psql -X -h localhost -U postgres -d $Database -Atq -v ON_ERROR_STOP=1 -c "SELECT count(*) || ' ' || coalesce(string_agg(c.oid::regclass::text, ', ' ORDER BY c.oid::regclass::text), '') FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname IN ('laplace', 'realize', 'consensus', 'generation') AND c.relkind IN ('r', 'p') AND NOT has_table_privilege('$role', c.oid, 'MAINTAIN')"
    if ($LASTEXITCODE) { throw "declared access: could not read the MAINTAIN privileges of $role in $Database" }
    $count, $names = "$denied".Split(' ', 2)
    if ($count -ne '0') { throw "declared access: $role may not MAINTAIN $count substrate table(s) of ${Database}, so its ANALYZE would skip them: $names" }
    "declared access: $role may MAINTAIN every substrate table and partition of $Database"
}
