#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$BuildDirectory
)
$ErrorActionPreference='Stop'
$cache=Join-Path $BuildDirectory 'CMakeCache.txt'
if(-not(Test-Path -LiteralPath $cache -PathType Leaf)){exit 0}
$entry=@(Get-Content -LiteralPath $cache | Where-Object {$_ -match '^CMAKE_HOME_DIRECTORY:INTERNAL='})
if($entry.Count -ne 1){throw "Cannot identify the source owning $cache"}
$owner=[IO.Path]::GetFullPath($entry[0].Split('=',2)[1]).TrimEnd('\','/')
$expected=[IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\','/')
if(-not [StringComparer]::OrdinalIgnoreCase.Equals($owner,$expected)){
    throw "Build directory $BuildDirectory belongs to $owner, not $expected. Select this checkout's build directory."
}
