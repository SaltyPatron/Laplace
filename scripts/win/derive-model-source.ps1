# Derives a model's registered source NAME from its snapshot directory path.
# Must stay in exact parity with ModelDecomposer.DeriveModelName (app/
# Laplace.Decomposers/Model/ModelDecomposer.cs): a HF hub segment
# "models--org--name" becomes "org/name"; otherwise the last path segment that
# is neither "snapshots" nor a >=32-char hex revision. seed-step.cmd uses this
# for post-step verification (evidence_count by source_id(name)).
param([Parameter(Mandatory = $true)][string]$ModelDir)

$segs = $ModelDir.Replace('\', '/').TrimEnd('/').Split('/') | Where-Object { $_ -ne '' }
$hub = $segs | Where-Object { $_ -like 'models--*' } | Select-Object -First 1
if ($hub) {
    ($hub.Substring(8) -split '--') -join '/'
    exit 0
}
for ($i = $segs.Length - 1; $i -ge 0; $i--) {
    $s = $segs[$i]
    if ($s -eq 'snapshots') { continue }
    if ($s.Length -ge 32 -and $s -match '^[0-9a-fA-F]+$') { continue }
    $s
    exit 0
}
'model'
