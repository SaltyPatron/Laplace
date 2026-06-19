param(

    [Parameter(Mandatory = $true)][string] $Key,

    [Parameter(Mandatory = $true)][string] $Src,

    [Parameter(Mandatory = $true)][int] $Layer

)



$ErrorActionPreference = 'Stop'

$psql = if (Get-Command psql -ErrorAction SilentlyContinue) { 'psql' } else { 'C:\Program Files\PostgreSQL\18\bin\psql.exe' }

if (-not $env:PGPASSWORD) { $env:PGPASSWORD = 'postgres' }



if ($Key -eq 'document') {

    $q = @"

SELECT EXISTS(

  SELECT 1 FROM laplace.physicalities p

  WHERE p.source_id = laplace.source_id('$Src')

  LIMIT 1)

"@

} else {

    $q = @"

SELECT laplace.evidence_count(

  p_type => laplace.canonical_id('substrate/type/HasLayerCompleted/$Layer/v1'),

  p_source => laplace.source_id('$Src')) > 0

"@

}



$result = (& $psql -h localhost -U postgres -d laplace -tAc $q).Trim()

Write-Output "STAT_${Key}=$result"

