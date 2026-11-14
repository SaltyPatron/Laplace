param(

    [Parameter(Mandatory = $true)][string] $Key,

    [Parameter(Mandatory = $true)][string] $Src,

    [Parameter(Mandatory = $true)][int] $Layer,

    [string] $Dbname = $(if ($env:LAPLACE_DBNAME) { $env:LAPLACE_DBNAME } else { 'laplace' })

)



$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/machine-env.ps1"
$psql = Get-LaplacePostgresTool psql




if ($Key -eq 'document') {

    $q = @"

SELECT EXISTS(

  SELECT 1 FROM laplace.physicalities p

  WHERE p.source_id = public.blake3_send(public.laplace_text_id('substrate/source/$Src/v1'))

  LIMIT 1)

"@

} else {

    $q = @"

SELECT ops.layer_completed(public.blake3_send(public.laplace_text_id('substrate/source/$Src/v1')), $Layer)

"@

}



$result = (& $psql -h localhost -U postgres -d $Dbname -tAc $q).Trim()

Write-Output "STAT_${Key}=$result"

