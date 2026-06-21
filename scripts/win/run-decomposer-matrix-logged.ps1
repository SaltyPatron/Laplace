# Decomposer matrix runner — tees all output to .ingest-proof/decomposer-matrix-run.log
param(
    [string]$FromSource = "",
    [switch]$SkipDbIsolate
)

$ErrorActionPreference = "Stop"
$Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$LogDir = Join-Path $Root ".ingest-proof"
$Log = Join-Path $LogDir "decomposer-matrix-run.log"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$env:LAPLACE_SKIP_MODELS = "1"
$env:INGEST = "D:\Data\Ingest"
$env:LAPLACE_DATA_ROOT = "D:\Data\Ingest"

function Write-Log([string]$Line) {
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $msg = "[$ts] $Line"
    Add-Content -Path $Log -Value $msg -Encoding utf8
    Write-Host $msg
}

function Invoke-TeeCmd([string]$Label, [string]$Arguments) {
    Write-Log "==== START: $Label ===="
    $code = 0
    try {
        cmd /c $Arguments 2>&1 | ForEach-Object {
            $line = "$_"
            Add-Content -Path $Log -Value $line -Encoding utf8
            Write-Host $line
        }
        if ($LASTEXITCODE -ne $null) { $code = $LASTEXITCODE }
    }
    catch {
        Write-Log "==== ERROR: $Label — $_ ===="
        exit 1
    }
    if ($code -ne 0) {
        Write-Log "==== FAILED ($code): $Label ===="
        exit $code
    }
    Write-Log "==== OK: $Label ===="
}

Set-Location $Root
Write-Log "DECOMPOSER-MATRIX RUN START"
Write-Log "LAPLACE_SKIP_MODELS=1 INGEST=$($env:INGEST)"
if ($FromSource) { Write-Log "Resume from source: $FromSource" }
Write-Log "WordFrameNet vault: $(if (Test-Path 'D:\Data\Ingest\WordFrameNet') { 'present' } else { 'MISSING - expect wordframenet step to fail unless downloaded' })"

if (-not $SkipDbIsolate -and -not $FromSource) {
    Invoke-TeeCmd "db-isolate laplace" "call scripts\win\db-isolate.cmd laplace"
} else {
    Write-Log "Skipping db-isolate laplace (resume or explicit skip)"
}

$matrixArgs = "call scripts\win\decomposer-matrix.cmd"
if ($FromSource) { $matrixArgs += " --from $FromSource" }
Invoke-TeeCmd "decomposer-matrix" $matrixArgs

Write-Log "DECOMPOSER-MATRIX RUN COMPLETE"
exit 0
