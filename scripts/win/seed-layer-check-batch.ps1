param(
    [string] $Dbname = $(if ($env:LAPLACE_DBNAME) { $env:LAPLACE_DBNAME } else { 'laplace' })
)
$ErrorActionPreference = 'Stop'
$psql = if (Get-Command psql -ErrorAction SilentlyContinue) { 'psql' } else { 'C:\Program Files\PostgreSQL\18\bin\psql.exe' }
if (-not $env:PGPASSWORD) { $env:PGPASSWORD = 'postgres' }

# key -> (source_name, layer) — document uses physicalities probe
$layers = [ordered]@{
    unicode       = @('UnicodeDecomposer', 0)
    iso639        = @('ISO639Decomposer', 1)
    document      = @('UserPrompt', 2)
    wordnet       = @('WordNetDecomposer', 2)
    omw           = @('OMWDecomposer', 3)
    verbnet       = @('VerbNetDecomposer', 2)
    propbank      = @('PropBankDecomposer', 2)
    framenet      = @('FrameNetDecomposer', 3)
    mapnet        = @('MapNetDecomposer', 2)
    wordframenet  = @('WordFrameNetDecomposer', 2)
    semlink       = @('SemLinkDecomposer', 3)
    conceptnet    = @('ConceptNetDecomposer', 2)
    atomic2020    = @('Atomic2020Decomposer', 2)
    ud            = @('UDDecomposer', 2)
    wiktionary    = @('WiktionaryDecomposer', 2)
    tatoeba       = @('TatoebaDecomposer', 2)
    opensubtitles = @('OpenSubtitlesDecomposer', 2)
}

$parts = foreach ($kv in $layers.GetEnumerator()) {
    $key = $kv.Key
    $src = $kv.Value[0]
    $layer = $kv.Value[1]
    if ($key -eq 'document') {
        @"
SELECT 'STAT_document=' || EXISTS(
  SELECT 1 FROM laplace.physicalities p
  WHERE p.source_id = laplace.source_id('$src')
  LIMIT 1)::text
"@
    } else {
        @"
SELECT 'STAT_$key=' || (laplace.evidence_count(
  p_type => laplace.canonical_id('substrate/type/HasLayerCompleted/$layer/v1'),
  p_source => laplace.source_id('$src')) > 0)::text
"@
    }
}
$sql = ($parts -join "`nUNION ALL`n") + ";"
(& $psql -h localhost -U postgres -d $Dbname -tAc $sql) | ForEach-Object { $_.Trim() } | Where-Object { $_ }
