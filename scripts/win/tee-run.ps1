#requires -Version 7
# Live-tail a cmd.exe script: child writes the log; console tails it; exit code preserved.
# Avoids silent >log redirects that leave the Tony_* windows looking stalled.
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
New-Item -ItemType File -Path $LogPath -Force | Out-Null

# Entire command + redirect must be ONE /c argument so & chaining stays inside cmd.
$p = Start-Process -FilePath "cmd.exe" `
  -ArgumentList @("/c", "$CommandLine > `"$LogPath`" 2>&1") `
  -WorkingDirectory $WorkingDirectory `
  -PassThru -WindowStyle Hidden

$fs = $null
$sr = $null
try {
  while (-not $p.HasExited) {
    if ($null -eq $fs) {
      try {
        $fs = [System.IO.File]::Open(
          $LogPath,
          [System.IO.FileMode]::Open,
          [System.IO.FileAccess]::Read,
          [System.IO.FileShare]::ReadWrite)
        $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8, $true, 4096, $true)
      } catch {
        Start-Sleep -Milliseconds 50
        continue
      }
    }
    while ($null -ne ($line = $sr.ReadLine())) {
      Write-Host $line
    }
    Start-Sleep -Milliseconds 50
  }
  $null = $p.WaitForExit()
  if ($null -ne $sr) {
    while ($null -ne ($line = $sr.ReadLine())) {
      Write-Host $line
    }
  }
} finally {
  if ($null -ne $sr) { $sr.Dispose() }
  if ($null -ne $fs) { $fs.Dispose() }
}

exit $p.ExitCode
