#requires -Version 7
# Run a cmd.exe script ATTACHED to this console, teeing output live to the log.
# Attached = one console, one lifetime: Ctrl+C / closing the window terminates
# the actual work, not just a log tail — no hidden orphaned ingest trees.
# The child's exit code is preserved verbatim, including negative NTSTATUS
# crash codes (0xC0000005 access violation, 0xC000013A Ctrl+C) that a detached
# wrapper plus `if errorlevel 1` callers previously misread as success.
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$LogPath,
  [Parameter(Mandatory)][string]$WorkingDirectory,
  [Parameter(Mandatory)][string]$CommandLine
)
$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $WorkingDirectory

$logDir = Split-Path -Parent $LogPath
if ($logDir -and -not (Test-Path -LiteralPath $logDir)) {
  New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}
if (Test-Path -LiteralPath $LogPath) { Remove-Item -LiteralPath $LogPath -Force }

# Hand the command line to cmd through an environment variable: pwsh passes the
# bare token %LAPLACE_TEE_CMDLINE% (no spaces, so no re-quoting) and cmd expands
# it itself. Embedded quotes in $CommandLine never cross the pwsh->native
# argument-quoting boundary — the pwsh .cmd-launch mangling lived exactly there.
$env:LAPLACE_TEE_CMDLINE = "$CommandLine 2>&1"

# AutoFlush StreamWriter = true tee: every line hits the log the moment it hits
# the console, so a killed run's log ends where the run ended.
$sw = [System.IO.StreamWriter]::new($LogPath, $false, [System.Text.UTF8Encoding]::new($false))
$sw.AutoFlush = $true
try {
  & cmd.exe /c '%LAPLACE_TEE_CMDLINE%' | ForEach-Object {
    $sw.WriteLine($_)
    Write-Host $_
  }
} finally {
  $sw.Dispose()
}
exit $LASTEXITCODE
