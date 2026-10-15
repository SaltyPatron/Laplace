#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Origin,
    [Parameter(Mandatory)][string]$KeyFile,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$CommandDirectory
)
$ErrorActionPreference='Stop'
$runtime=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../runtime'))
if (-not [IO.Path]::IsPathFullyQualified($KeyFile) -or -not (Test-Path -LiteralPath $KeyFile -PathType Leaf)) {
    throw 'Select an existing absolute API-key file path.'
}
$configuration=[IO.Path]::GetTempFileName()
try {
    $profile=@{
        version=2
        default_profile='service'
        profiles=@{service=@{
            runtime=@{kind='directory';directory=$runtime}
            api=@{origin=$Origin;key_file=$KeyFile}
        }}
    }
    [IO.File]::WriteAllText($configuration,($profile | ConvertTo-Json -Depth 8))
    & "$PSScriptRoot/Install-LaplaceClient.ps1" -Configuration $configuration -Destination $Destination -CommandDirectory $CommandDirectory
} finally {
    [IO.File]::Delete($configuration)
}
