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
Set-LaplaceDefault LAPLACE_DATA_ROOT ([IO.Path]::Combine($env:LOCALAPPDATA, 'Laplace'))
Set-LaplaceDefault LAPLACE_SRC ([IO.Path]::Combine($env:USERPROFILE, 'source\repos'))
Set-LaplaceDefault LAPLACE_WORK ([IO.Path]::Combine($env:LAPLACE_DATA_ROOT, 'work'))
Set-LaplaceDefault LAPLACE_DATA ([IO.Path]::Combine($env:LAPLACE_DATA_ROOT, 'data'))
Set-LaplaceDefault LAPLACE_BUILD ([IO.Path]::Combine($env:LAPLACE_WORK, 'build'))
Set-LaplaceDefault LAPLACE_BUILD_ROOT ([IO.Path]::Combine($env:LAPLACE_BUILD, 'Laplace'))
Set-LaplaceDefault LAPLACE_DEPS ([IO.Path]::Combine($env:LAPLACE_DATA_ROOT, 'dependencies'))
Set-LaplaceDefault LAPLACE_DEPSRC ([IO.Path]::Combine($env:LAPLACE_DEPS, 'src'))
Set-LaplaceDefault LAPLACE_EXTERNAL $env:LAPLACE_DEPSRC
Set-LaplaceDefault LAPLACE_ENGINE_BUILD ([IO.Path]::Combine($env:LAPLACE_BUILD_ROOT, 'build-win'))
Set-LaplaceDefault LAPLACE_PGDATA ([IO.Path]::Combine($env:LAPLACE_DATA_ROOT, 'pgdata'))
Set-LaplaceDefault LAPLACE_PGWAL ([IO.Path]::Combine($env:LAPLACE_PGDATA, 'pg_wal'))
Set-LaplaceDefault LAPLACE_ROLE 'laplace'
Set-LaplaceDefault LAPLACE_PGUSER $env:LAPLACE_ROLE
Set-LaplaceDefault LAPLACE_PG_SERVICE_ACCOUNT 'NT AUTHORITY\NetworkService'
if ($ForCmd) {
    foreach ($name in $values.Keys) { '{0}={1}' -f $name, [Environment]::GetEnvironmentVariable($name) }
    'LAPLACE_CONFIG_SOURCE=' + $env:LAPLACE_CONFIG_SOURCE
    'LAPLACE_MACHINE_ENV_IMPORTED=1'
}
