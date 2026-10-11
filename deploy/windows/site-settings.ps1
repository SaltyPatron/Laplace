#Requires -Version 7
[CmdletBinding()]
param([switch]$ForCmd)
. "$PSScriptRoot/../../scripts/win/machine-env.ps1"
$ErrorActionPreference='Stop'
$site=if($env:LAPLACE_IIS_SITE){$env:LAPLACE_IIS_SITE}else{'Laplace'}
$pool=if($env:LAPLACE_IIS_POOL){$env:LAPLACE_IIS_POOL}else{'LaplacePool'}
$path=if($env:LAPLACE_IIS_API){$env:LAPLACE_IIS_API}else{Join-Path $env:LAPLACE_DATA_ROOT 'iis/laplace-api'}
$base=$env:LAPLACE_PUBLIC_BASE_URL
if (-not $base) { throw 'Declare LAPLACE_PUBLIC_BASE_URL in the installation machine.env before installing or publishing IIS.' }
$origin=$null
if (-not [Uri]::TryCreate($base,[UriKind]::Absolute,[ref]$origin) -or
    ($origin.Scheme -ne 'https' -and -not ($origin.Scheme -eq 'http' -and $origin.IsLoopback)) -or
    $origin.AbsolutePath -ne '/' -or $origin.UserInfo -or $origin.Query -or $origin.Fragment) {
    throw 'LAPLACE_PUBLIC_BASE_URL must be an HTTPS origin (HTTP is allowed only for loopback development).'
}
if ($site -notmatch '^[A-Za-z0-9._-]+$' -or $pool -notmatch '^[A-Za-z0-9._-]+$' -or
    -not [IO.Path]::IsPathFullyQualified($path) -or $path -match '["\r\n%&|<>^!]') {
    throw 'Invalid IIS site, pool or absolute installation path.'
}
$settings=[ordered]@{site=$site;pool=$pool;physical_path=[IO.Path]::GetFullPath($path);
    browser_origin=$origin.GetLeftPart([UriPartial]::Authority);protocol=$origin.Scheme;
    hostname=$origin.DnsSafeHost;port=$origin.Port;certificate_thumbprint=$env:LAPLACE_IIS_CERTIFICATE_THUMBPRINT;
    configuration_source=$env:LAPLACE_CONFIG_SOURCE}
if ($ForCmd) {
    "LAPLACE_IIS_SITE=$site"
    "LAPLACE_IIS_POOL=$pool"
    "LAPLACE_IIS_API=$($settings.physical_path)"
    "LAPLACE_SITE_URL=$($settings.browser_origin)"
    'LAPLACE_IIS_SETTINGS_RESOLVED=1'
} else { $settings | ConvertTo-Json }
