param(
    [string]$LogPath,
    [string]$InputFile
)

$text = Get-Content -Raw -Path $LogPath -ErrorAction SilentlyContinue
if (-not $text) { Write-Output "ERROR: empty log $LogPath"; exit 1 }

function Get-Metric([string]$pattern) {
    if ($text -match $pattern) { return $Matches[1] } else { return '' }
}

$wall = Get-Metric 'WALL_CLOCK_SEC=([0-9.]+)'
if (-not $wall) {
    $wall = Get-Metric 'done:.*?\s([0-9.]+)s\s*$'
}

$inputUnits = Get-Metric 'input_units=(\d+)'
if (-not $inputUnits) { $inputUnits = Get-Metric 'INGEST_START[^\n]*input_units=(\d+)' }
$intents = Get-Metric 'done: ([0-9,]+) intents'
$entities = Get-Metric 'done: [0-9,]+ intents applied, ([0-9,]+) novel entities'
$phys = Get-Metric 'done: [0-9,]+ intents applied, [0-9,]+ novel entities, ([0-9,]+) physicalities'
$rts = Get-Metric 'done:.*?([0-9,]+) round-trips'
$entSkip = Get-Metric 'entities_skipped=(\d+)'
$physSkip = Get-Metric 'physicalities_skipped=(\d+)'
$consensus = Get-Metric 'consensus: ([0-9,]+) relations materialized'
$ingestComplete = Get-Metric 'INGEST_COMPLETE[^\n]*elapsed_s=([0-9.]+)'

$inputBytes = 0
if (Test-Path $InputFile) {
    $inputBytes = [long](Get-Content $InputFile | Where-Object { $_ -match '^total_bytes=' } | ForEach-Object { $_ -replace '^total_bytes=', '' } | Select-Object -First 1)
}

function ToLong([string]$s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return 0 }
    return [long]($s -replace ',', '')
}

$wallD = if ($wall) { [double]$wall } else { 0.0 }
$units = ToLong $inputUnits
$mbps = if ($wallD -gt 0 -and $inputBytes -gt 0) { [math]::Round(($inputBytes / 1MB) / $wallD, 1) } else { 0 }
$rowsPerSec = if ($wallD -gt 0 -and $units -gt 0) { [math]::Round($units / $wallD, 0) } else { 0 }

Write-Output "wall_clock_sec=$wall"
Write-Output "ingest_elapsed_s=$ingestComplete"
Write-Output "input_units=$units"
Write-Output "input_bytes=$inputBytes"
Write-Output "mb_per_sec=$mbps"
Write-Output "units_per_sec=$rowsPerSec"
Write-Output "intents_applied=$(ToLong $intents)"
Write-Output "entities_inserted=$(ToLong $entities)"
Write-Output "physicalities_inserted=$(ToLong $phys)"
Write-Output "round_trips=$(ToLong $rts)"
Write-Output "entities_skipped=$(ToLong $entSkip)"
Write-Output "physicalities_skipped=$(ToLong $physSkip)"
Write-Output "consensus_relations=$(ToLong $consensus)"
