#requires -RunAsAdministrator
<#
.SYNOPSIS
  One-time elevated IIS wiring for the Laplace API: verify the .NET Hosting
  Bundle / ANCM is registered, then create the app pool + site. Idempotent.
  Driven entirely through appcmd.exe so it works identically under Windows
  PowerShell 5.1 and PowerShell 7 (the WebAdministration `IIS:` PSDrive is not
  available in the PS7 compat session). Run publish.ps1 to fill the site folder.
#>
[CmdletBinding()]
param(
  [string]$SiteName     = "Laplace",
  [string]$PoolName     = "LaplacePool",
  [int]   $Port         = 8080,
  [string]$PhysicalPath = "C:\inetpub\laplace-api"
)
$ErrorActionPreference = "Stop"
$appcmd = "$env:windir\system32\inetsrv\appcmd.exe"

function Invoke-AppCmd {
  param([Parameter(ValueFromRemainingArguments)] [string[]]$Args)
  $out = & $appcmd @Args 2>&1
  if ($LASTEXITCODE -ne 0) { throw "appcmd $($Args -join ' ') -> $out" }
  return $out
}

Write-Host "==> verify ASP.NET Core Module V2 (Hosting Bundle)" -ForegroundColor Cyan
if (-not ((& $appcmd list module /name:AspNetCoreModuleV2 2>$null) -match 'AspNetCoreModuleV2')) {
  throw "AspNetCoreModuleV2 not registered in IIS. Install the .NET 10 Hosting Bundle (winget install Microsoft.DotNet.HostingBundle.10) then 'iisreset'."
}

New-Item -ItemType Directory $PhysicalPath -Force | Out-Null

Write-Host "==> app pool '$PoolName' (No Managed Code, AlwaysRunning)" -ForegroundColor Cyan
if (-not (& $appcmd list apppool /name:$PoolName 2>$null)) {
  Invoke-AppCmd add apppool /name:$PoolName | Out-Null
}
# managedRuntimeVersion="" => "No Managed Code" (correct for ASP.NET Core / ANCM).
Invoke-AppCmd set apppool /apppool.name:$PoolName /managedRuntimeVersion:"" /startMode:AlwaysRunning /autoStart:true | Out-Null

Write-Host "==> site '$SiteName' on port $Port -> $PhysicalPath" -ForegroundColor Cyan
if (-not (& $appcmd list site /name:$SiteName 2>$null)) {
  Invoke-AppCmd add site /name:$SiteName /bindings:"http/*:${Port}:" /physicalPath:$PhysicalPath | Out-Null
} else {
  Invoke-AppCmd set site /site.name:$SiteName /"[path='/'].[path='/'].physicalPath:$PhysicalPath" | Out-Null
}
# Bind the site's root application to our pool + enable preload.
Invoke-AppCmd set app /app.name:"$SiteName/" /applicationPool:$PoolName /preloadEnabled:true | Out-Null

& $appcmd start apppool /apppool.name:$PoolName 2>$null | Out-Null
& $appcmd start site    /site.name:$SiteName    2>$null | Out-Null
Write-Host "OK IIS site '$SiteName' ready at http://localhost:$Port" -ForegroundColor Green
