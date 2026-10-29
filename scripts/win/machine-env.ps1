# Read installation configuration as data, never as PowerShell or batch commands.
[CmdletBinding()]
param([switch]$ForCmd)
$ErrorActionPreference = 'Stop'
$file = if ($env:LAPLACE_MACHINE_ENV) { $env:LAPLACE_MACHINE_ENV } else { Join-Path $env:ProgramData 'Laplace\machine.env' }
if ($env:LAPLACE_MACHINE_ENV -and -not (Test-Path -LiteralPath $file -PathType Leaf)) {
    throw "LAPLACE_MACHINE_ENV does not name a file: $file"
}
$values = [ordered]@{}
if (Test-Path -LiteralPath $file -PathType Leaf) {
    foreach ($line in Get-Content -LiteralPath $file) {
        if ($line -match '^\s*(#|$)') { continue }
        if ($line -notmatch '^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$') { throw "Invalid configuration line in $file" }
        $name = $Matches[1]; $value = $Matches[2]
        if ($values.Contains($name)) { throw "Duplicate configuration key: $name" }
        if ($value.Length -ge 2 -and $value[0] -in '"', "'" -and $value[-1] -eq $value[0]) { $value = $value.Substring(1, $value.Length - 2) }
        $value = [Environment]::ExpandEnvironmentVariables($value)
        # These values cross the CMD boundary as quoted SET arguments.
        if ($value -match '["\r\n]') { throw "Unsupported quote or newline in configuration key: $name" }
        $values[$name] = $value
        if (-not [Environment]::GetEnvironmentVariable($name)) { [Environment]::SetEnvironmentVariable($name, $value) }
    }
    $env:LAPLACE_CONFIG_SOURCE = [IO.Path]::GetFullPath($file)
} else { $env:LAPLACE_CONFIG_SOURCE = 'defaults' }
# Defaults name roles, not a developer's drives. The installer records explicit
# roots on a managed host; process overrides take precedence over that record.
function Set-LaplaceDefault([string]$Name, [string]$Value) {
    if (-not [Environment]::GetEnvironmentVariable($Name)) { [Environment]::SetEnvironmentVariable($Name, $Value) }
    if (-not $values.Contains($Name)) { $values[$Name] = [Environment]::GetEnvironmentVariable($Name) }
}
Set-LaplaceDefault LAPLACE_SRC ([IO.Path]::Combine($env:USERPROFILE, 'source\repos'))
Set-LaplaceDefault LAPLACE_NATIVE ([IO.Path]::Combine($env:LAPLACE_SRC, 'Laplace-Native'))
Set-LaplaceDefault LAPLACE_DATA_ROOT ([IO.Path]::Combine($env:LOCALAPPDATA, 'Laplace'))
Set-LaplaceDefault LAPLACE_DEPLOY ([IO.Path]::Combine($env:LAPLACE_DATA_ROOT, 'deploy'))
Set-LaplaceDefault LAPLACE_WORK ([IO.Path]::Combine($env:LOCALAPPDATA, 'Laplace', 'work'))
# Corpus/model roots are declared independently in machine.env; never derive them from state.
Set-LaplaceDefault LAPLACE_BUILD ([IO.Path]::Combine($env:LAPLACE_WORK, 'build'))
$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\').ToLowerInvariant()
$workspaceId = [IO.Path]::GetFileName($checkout) + '-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($checkout))).Substring(0,12).ToLowerInvariant()
$canonical=[IO.Path]::GetFullPath([IO.Path]::Combine($env:LAPLACE_SRC,'Laplace')).TrimEnd('\').ToLowerInvariant()
$buildDefault=if($checkout -eq $canonical){[IO.Path]::Combine($env:LAPLACE_BUILD,'Laplace')}else{[IO.Path]::Combine($env:LAPLACE_BUILD,'worktrees',$workspaceId)}
Set-LaplaceDefault LAPLACE_BUILD_ROOT $buildDefault
Set-LaplaceDefault LAPLACE_DEPS ([IO.Path]::Combine($env:LAPLACE_BUILD, 'dependencies'))
Set-LaplaceDefault LAPLACE_DEPSRC ([IO.Path]::Combine($env:LAPLACE_DEPS, 'src'))
Set-LaplaceDefault LAPLACE_EXTERNAL $env:LAPLACE_DEPSRC
Set-LaplaceDefault LAPLACE_BLAKE3_DIR ([IO.Path]::Combine($env:LAPLACE_DEPSRC, 'blake3/c'))
Set-LaplaceDefault LAPLACE_COREMATH ([IO.Path]::Combine($env:LAPLACE_DEPSRC, 'core-math'))
Set-LaplaceDefault LAPLACE_TREESITTER ([IO.Path]::Combine($env:LAPLACE_DEPSRC, 'tree-sitter'))
Set-LaplaceDefault LAPLACE_OPERATIONS ([IO.Path]::Combine($env:LAPLACE_SRC, 'Laplace-Operations'))
Set-LaplaceDefault LAPLACE_TIER0 ([IO.Path]::Combine($env:LAPLACE_WORK, 'tier0/tier0.bin'))
Set-LaplaceDefault LAPLACE_ENGINE_BUILD ([IO.Path]::Combine($env:LAPLACE_BUILD_ROOT, 'build-win'))
Set-LaplaceDefault LAPLACE_PGDATA ([IO.Path]::Combine($env:LAPLACE_WORK, 'pgdata'))
Set-LaplaceDefault LAPLACE_PGWAL ([IO.Path]::Combine($env:LAPLACE_PGDATA, 'pg_wal'))
Set-LaplaceDefault LAPLACE_ROLE 'laplace'
Set-LaplaceDefault LAPLACE_PGUSER $env:LAPLACE_ROLE
Set-LaplaceDefault LAPLACE_PG_SERVICE_ACCOUNT 'NT AUTHORITY\NetworkService'
Set-LaplaceDefault LAPLACE_DEPENDENCY_MANIFEST ([IO.Path]::Combine($env:ProgramData, 'Laplace\dependencies.tsv'))
if (Test-Path -LiteralPath $env:LAPLACE_DEPENDENCY_MANIFEST -PathType Leaf) {
    $prefixes = @()
    foreach ($line in Get-Content -LiteralPath $env:LAPLACE_DEPENDENCY_MANIFEST) {
        if (-not $line.Trim() -or $line.StartsWith('#')) { continue }
        $fields = $line -split "`t"
        if ($fields.Count -lt 9 -or ($fields[8] -split ',') -notcontains 'windows' -or $fields[2] -eq 'tool') { continue }
        if ($fields[0] -notmatch '^[A-Za-z0-9_-]+$' -or $fields[1] -notmatch '^[A-Za-z0-9._-]+$' -or $fields[1] -eq '..') { throw 'Invalid installed dependency manifest' }
        $prefix = [IO.Path]::Combine($env:LAPLACE_DEPS, $fields[0], $fields[1])
        Set-LaplaceDefault ('LAPLACE_DEPENDENCY_' + ($fields[0] -replace '-', '_').ToUpperInvariant() + '_PREFIX') $prefix
        if ($fields[0] -eq 'postgresql') { Set-LaplaceDefault LAPLACE_PG_DIR $prefix }
        if (Test-Path -LiteralPath $prefix -PathType Container) { $prefixes += $prefix }
        if ($fields[0] -eq 'icu') {
            Set-LaplaceDefault LAPLACE_ICU_DIR $prefix
            Set-LaplaceDefault ICU_ROOT $prefix
            Set-LaplaceDefault CMAKE_LIBRARY_PATH ([IO.Path]::Combine($prefix, 'lib64'))
        }
    }
    $env:CMAKE_PREFIX_PATH = (@(($env:CMAKE_PREFIX_PATH -split ';') + $prefixes) | Where-Object { $_ } | Select-Object -Unique) -join ';'
    $values['CMAKE_PREFIX_PATH'] = $env:CMAKE_PREFIX_PATH
    $runtimeDirs = foreach ($prefix in $prefixes) { foreach ($leaf in 'bin','bin64') {
        $directory = [IO.Path]::Combine($prefix, $leaf)
        if (Test-Path -LiteralPath $directory -PathType Container) { $directory }
    } }
    $env:PATH = (@($runtimeDirs) + @($env:PATH -split ';') | Select-Object -Unique) -join ';'
    $values['PATH'] = $env:PATH
}
function Get-LaplacePostgresTool([string]$Name) {
    if ($Name -notmatch '^[a-z_]+$' -or -not $env:LAPLACE_PG_DIR) { throw 'Declare LAPLACE_PG_DIR in the machine configuration.' }
    $tool = Join-Path $env:LAPLACE_PG_DIR "bin/$Name.exe"
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "The declared PostgreSQL tool is missing: $tool" }
    return $tool
}
if ($ForCmd) {
    foreach ($name in $values.Keys) { '{0}={1}' -f $name, [Environment]::GetEnvironmentVariable($name) }
    'LAPLACE_CONFIG_SOURCE=' + $env:LAPLACE_CONFIG_SOURCE
    'LAPLACE_MACHINE_ENV_IMPORTED=1'
}
