#requires -Version 7
<#
  The managed services on Windows: the counterpart of deploy/linux/managed-services (laplace-lichess.service,
  laplace-mcp.service). Each is published from the repository into LAPLACE_OUT\<name> with the engine's run-time DLLs
  beside it, and runs as a Windows service (NSSM) under NT AUTHORITY\LocalService, the identity the database host
  authenticates as the role over loopback (Laplace-Operations setup.ps1 access: SSPI, pg_ident; LAPLACE_PG_IDENTITIES
  names "LOCAL SERVICE") -- no password anywhere, as the Linux units carry none (peer socket). What a unit file
  declares is declared here the same way: the database route, the secrets file, the log and temp directories, restart.
    scripts\win\ensure-managed-services.cmd            (publish-deploy.cmd calls it after deploy-api.cmd)
  Elevation installs or changes a service; without it an installed service is only reported.
#>
[CmdletBinding()]
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
  [string]$NssmExe = "D:\NSSM\nssm-2.24\win64\nssm.exe",
  [string]$LogDir = "D:\Data\Output",
  [switch]$SkipPublish
)
$ErrorActionPreference = "Stop"
if (-not $env:LAPLACE_OUT) { throw "Run through scripts\win\ensure-managed-services.cmd (env.cmd declares LAPLACE_OUT and the build roots)" }
if (-not (Test-Path -LiteralPath $NssmExe)) { throw "nssm.exe missing: $NssmExe" }
$isAdmin = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$secrets = Join-Path $RepoRoot "deploy\secrets"
# The database route of a managed service: the host-authenticated loopback route (ManagedServiceDatabase.cs refuses any other)
$dbName = if ($env:LAPLACE_DBNAME) { $env:LAPLACE_DBNAME } else { "laplace" }
$dbRole = if ($env:LAPLACE_PGUSER) { $env:LAPLACE_PGUSER } else { "laplace" }
$db = "Host=127.0.0.1;Port=5432;Username=$dbRole;Database=$dbName"

function Read-EnvFile([string]$path) {
  $map = [ordered]@{}
  if (-not (Test-Path -LiteralPath $path)) { return $map }
  foreach ($line in Get-Content -LiteralPath $path) {
    $t = $line.Trim(); if (-not $t -or $t.StartsWith("#")) { continue }
    $eq = $t.IndexOf("="); if ($eq -lt 1) { continue }
    $map[$t.Substring(0, $eq).Trim()] = $t.Substring($eq + 1).Trim().Trim("'").Trim('"')
  }
  return $map
}
function Publish-Service([string]$project, [string]$out, [string]$serviceName) {
  if ($SkipPublish -and (Test-Path -LiteralPath $out)) { return }
  # a running service holds its files: stopped before its tree is written (Ensure-Service starts it again)
  if ($isAdmin -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { & $NssmExe stop $serviceName confirm 2>$null | Out-Null }
  & dotnet publish (Join-Path $RepoRoot "app\$project\$project.csproj") -c Release --no-self-contained -o $out --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "publish of $project failed" }
  foreach ($d in "core\laplace_core", "dynamics\laplace_dynamics", "synthesis\laplace_synthesis", "core\laplace_syzygy") {
    $dll = Join-Path $env:LAPLACE_ENGINE_BUILD "$d.dll"
    if (-not (Test-Path -LiteralPath $dll)) { throw "missing $dll (build-engine.cmd)" }
    Copy-Item -LiteralPath $dll -Destination $out -Force
  }
  & cmd /c "`"$PSScriptRoot\engine-runtime.cmd`" `"$out`""
  if ($LASTEXITCODE -ne 0) { throw "engine run-time DLLs for $project failed" }
  & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "publish-zstd.ps1") -Destination $out
  if ($LASTEXITCODE -ne 0) { throw "zstd for $project failed" }
}
function Ensure-Service([string]$name, [string]$display, [string]$exe, [string]$arguments, [string[]]$environment, [string]$workDir, [string]$probe) {
  $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
  if (-not $isAdmin) {
    if ($svc) { Write-Host "[managed-services] $name is $($svc.Status) (elevation needed to declare or restart it)" }
    else { Write-Warning "[managed-services] $name not installed - run elevated: scripts\win\ensure-managed-services.cmd" }
    return
  }
  New-Item -ItemType Directory -Force -Path $LogDir, $workDir | Out-Null
  if (-not $svc) { & $NssmExe install $name $exe | Out-Null }
  else { & $NssmExe stop $name confirm 2>$null | Out-Null }
  & $NssmExe set $name Application $exe | Out-Null
  if ($arguments) { & $NssmExe set $name AppParameters $arguments | Out-Null } else { & $NssmExe reset $name AppParameters 2>$null | Out-Null }
  & $NssmExe set $name AppDirectory (Split-Path -Parent $exe) | Out-Null
  & $NssmExe set $name DisplayName $display | Out-Null
  & $NssmExe set $name Description "Laplace managed service (deploy/linux/managed-services on Linux; scripts/win/ensure-managed-services.ps1 here)" | Out-Null
  & $NssmExe set $name ObjectName "NT AUTHORITY\LocalService" | Out-Null
  & $NssmExe set $name Start SERVICE_AUTO_START | Out-Null
  & $NssmExe set $name AppStdout (Join-Path $LogDir "$name.out.log") | Out-Null
  & $NssmExe set $name AppStderr (Join-Path $LogDir "$name.err.log") | Out-Null
  & $NssmExe set $name AppRotateFiles 1 | Out-Null
  & $NssmExe set $name AppRotateBytes 1048576 | Out-Null
  & $NssmExe set $name AppExit Default Restart | Out-Null
  & $NssmExe set $name AppRestartDelay 10000 | Out-Null
  # the whole environment, as the unit file's Environment= and EnvironmentFile= lines: never PGPASSWORD or PGPASSFILE
  & $NssmExe set $name AppEnvironmentExtra @($environment) | Out-Null
  & $NssmExe start $name | Out-Null
  $ok = $false
  foreach ($i in 1..30) {
    Start-Sleep -Seconds 1
    try { $null = Invoke-RestMethod -Uri $probe -TimeoutSec 5; $ok = $true; break } catch { }
  }
  $svc = Get-Service -Name $name
  if ($ok) { Write-Host "[managed-services] $name $($svc.Status); $probe answers" }
  else { Write-Warning "[managed-services] $name $($svc.Status); $probe did not answer in 30s - see $LogDir\$name.err.log" }
}

$common = @(
  "LAPLACE_DB=$db",
  "LAPLACE_EXTERNAL=$env:LAPLACE_EXTERNAL",
  "LAPLACE_OPS_LOG_DIR=$LogDir"
)
$apiEnv = Read-EnvFile (Join-Path $RepoRoot "deploy\windows\laplace-api.env")
if ($apiEnv.Contains("LAPLACE_PERFCACHE_BIN")) { $common += "LAPLACE_PERFCACHE_BIN=$($apiEnv['LAPLACE_PERFCACHE_BIN'])" }
foreach ($kv in (Read-EnvFile (Join-Path $secrets "chess-lab.env")).GetEnumerator()) { $common += "$($kv.Key)=$($kv.Value)" }

# Lichess: laplace-lichess.service. Its token file is required there and here.
$lichessSecrets = Read-EnvFile (Join-Path $secrets "lichess.env")
if (-not $lichessSecrets.Count) { throw "deploy\secrets\lichess.env missing - scripts\win\sync-operator-secrets.cmd" }
$lichessOut = Join-Path $env:LAPLACE_OUT "lichess"
Publish-Service "Laplace.Endpoints.Lichess" $lichessOut "LaplaceLichess"
$work = Join-Path $env:LAPLACE_BUILD_ROOT "work\lichess"
$envLichess = $common + @("TMPDIR=$work", "TMP=$work", "TEMP=$work")
foreach ($kv in $lichessSecrets.GetEnumerator()) { $envLichess += "$($kv.Key)=$($kv.Value)" }
foreach ($kv in (Read-EnvFile (Join-Path $secrets "lichess-service.env")).GetEnumerator()) { $envLichess += "$($kv.Key)=$($kv.Value)" }
Ensure-Service "LaplaceLichess" "Laplace Lichess bot (managed)" (Join-Path $lichessOut "Laplace.Endpoints.Lichess.exe") "" $envLichess $work "http://127.0.0.1:5189/health/live"

# MCP over HTTP: laplace-mcp.service, only once its secrets exist (LAPLACE_MCP_TOKEN in deploy\secrets\mcp.env).
$mcpSecrets = Read-EnvFile (Join-Path $secrets "mcp.env")
if ($mcpSecrets.Count) {
  $mcpOut = Join-Path $env:LAPLACE_OUT "mcp"
  Publish-Service "Laplace.Endpoints.Mcp" $mcpOut "LaplaceMcp"
  $work = Join-Path $env:LAPLACE_BUILD_ROOT "work\mcp"
  $envMcp = $common + @("TMPDIR=$work", "TMP=$work", "TEMP=$work")
  foreach ($kv in $mcpSecrets.GetEnumerator()) { $envMcp += "$($kv.Key)=$($kv.Value)" }
  foreach ($kv in (Read-EnvFile (Join-Path $secrets "agents.env")).GetEnumerator()) { $envMcp += "$($kv.Key)=$($kv.Value)" }
  Ensure-Service "LaplaceMcp" "Laplace MCP (managed, Streamable HTTP)" (Join-Path $mcpOut "Laplace.Endpoints.Mcp.exe") "--http" $envMcp $work "http://127.0.0.1:5188/health/live"
} else {
  Write-Host "[managed-services] LaplaceMcp not declared: no deploy\secrets\mcp.env (LAPLACE_MCP_TOKEN)"
}
