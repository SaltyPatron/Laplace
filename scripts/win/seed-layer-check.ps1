param(

    [Parameter(Mandatory = $true)][string] $Key,

    [Parameter(Mandatory = $true)][string] $Src,

    [Parameter(Mandatory = $true)][int] $Layer,

    [string] $Dbname = $(if ($env:LAPLACE_DBNAME) { $env:LAPLACE_DBNAME } else { 'laplace' })

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

SELECT ops.layer_completed(laplace.source_id('$Src'), $Layer)

"@

}



$result = (& $psql -h localhost -U postgres -d $Dbname -tAc $q).Trim()

Write-Output "STAT_${Key}=$result"

