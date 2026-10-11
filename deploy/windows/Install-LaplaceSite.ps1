#requires -Version 7
[CmdletBinding()]
param([switch]$Plan)
$ErrorActionPreference = "Stop"
$settings=(& "$PSScriptRoot/site-settings.ps1" | ConvertFrom-Json)
if ($Plan) { $settings | ConvertTo-Json; return }
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'IIS bindings require an elevated setup operation.'
}
$SiteName=$settings.site; $PoolName=$settings.pool; $Port=$settings.port; $PhysicalPath=$settings.physical_path
$binding="$($settings.protocol)/*:${Port}:$($settings.hostname)"
$certificate=$null
if ($settings.protocol -eq 'https') {
  if ($settings.certificate_thumbprint -notmatch '^[a-fA-F0-9]{40}$') { throw 'Declare LAPLACE_IIS_CERTIFICATE_THUMBPRINT for the installed LocalMachine/My certificate.' }
  $certificate=Get-Item "Cert:/LocalMachine/My/$($settings.certificate_thumbprint)"
  $names=@($certificate.DnsNameList | ForEach-Object { $_.Unicode.ToLowerInvariant() })
  $hostName=$settings.hostname.ToLowerInvariant()
  $wildcard=if ($hostName.Contains('.')) { '*.' + $hostName.Substring($hostName.IndexOf('.')+1) } else { '' }
  if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date) -or
      ($names -notcontains $hostName -and $names -notcontains $wildcard)) { throw 'The declared certificate does not cover this origin with a valid private key.' }
}
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

# The site's web.config maps the aspNetCore handler (every ASP.NET Core publish does); IIS refuses a site-level
# <handlers> while the section is locked at the server (overrideModeDefault="Deny", IIS's default), and answers every
# request 500 with no body. Unlocked here, once, as part of the site's declaration.
Write-Host "==> unlock system.webServer/handlers for site-level web.config" -ForegroundColor Cyan
Invoke-AppCmd unlock config "-section:system.webServer/handlers" | Out-Null

Write-Host "==> app pool '$PoolName' (No Managed Code, AlwaysRunning)" -ForegroundColor Cyan
if (-not (& $appcmd list apppool /name:$PoolName 2>$null)) {
  Invoke-AppCmd add apppool /name:$PoolName | Out-Null
}
Invoke-AppCmd set apppool /apppool.name:$PoolName /managedRuntimeVersion:"" /startMode:AlwaysRunning /autoStart:true | Out-Null

Write-Host "==> site '$SiteName' on port $Port -> $PhysicalPath" -ForegroundColor Cyan
if (-not (& $appcmd list site /name:$SiteName 2>$null)) {
  Invoke-AppCmd add site /name:$SiteName /bindings:$binding /physicalPath:$PhysicalPath | Out-Null
} else {
  Invoke-AppCmd set site /site.name:$SiteName /"[path='/'].[path='/'].physicalPath:$PhysicalPath" | Out-Null
  Invoke-AppCmd set site /site.name:$SiteName /bindings:$binding | Out-Null
}
Invoke-AppCmd set app /app.name:"$SiteName/" /applicationPool:$PoolName /preloadEnabled:true | Out-Null

if ($certificate) {
  Add-Type -Path "$env:windir/System32/inetsrv/Microsoft.Web.Administration.dll"
  $manager=[Microsoft.Web.Administration.ServerManager]::new()
  try {
    $https=$manager.Sites[$SiteName].Bindings[0]
    $https.CertificateHash=$certificate.GetCertHash()
    $https.CertificateStoreName='My'
    $https.SslFlags=[Microsoft.Web.Administration.SslFlags]::Sni
    $manager.CommitChanges()
  } finally { $manager.Dispose() }
}

& $appcmd start apppool /apppool.name:$PoolName 2>$null | Out-Null
& $appcmd start site    /site.name:$SiteName    2>$null | Out-Null
$actualPath=(Invoke-AppCmd list vdir "$SiteName/" /text:physicalPath).Trim()
$actualBindings=(Invoke-AppCmd list site $SiteName /text:bindings).Trim()
if ($actualPath -ne $PhysicalPath -or $actualBindings -ne $binding) { throw 'Installed IIS path or binding differs from the declaration.' }
Write-Host "IIS route installed: $($settings.browser_origin) -> $PhysicalPath. Application readiness requires publishing and verification."
