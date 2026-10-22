#Requires -Version 7
$ErrorActionPreference='Stop'
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
$names='LAPLACE_MACHINE_ENV','LAPLACE_PUBLIC_BASE_URL','LAPLACE_WORK','LAPLACE_IIS_API','LAPLACE_IIS_SITE','LAPLACE_IIS_POOL'
$saved=@{}; foreach($name in $names) { $saved[$name]=[Environment]::GetEnvironmentVariable($name) }
$work=Join-Path $(if($env:LAPLACE_WORK){$env:LAPLACE_WORK}else{Join-Path $env:LOCALAPPDATA 'Laplace/work'}) ('iis-settings-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $work)
try {
    $env:LAPLACE_MACHINE_ENV=Join-Path $work 'machine.env'
    $root=Join-Path $work 'alternate root'
    "LAPLACE_WORK=$root" | Set-Content -LiteralPath $env:LAPLACE_MACHINE_ENV
    foreach($name in $names | Where-Object { $_ -ne 'LAPLACE_MACHINE_ENV' }) { [Environment]::SetEnvironmentVariable($name,$null) }
    $env:LAPLACE_PUBLIC_BASE_URL='https://dev.example.test'
    $settings=(& pwsh -NoProfile -File "$repo/deploy/windows/Install-LaplaceSite.ps1" -Plan | ConvertFrom-Json)
    if ($LASTEXITCODE -or $settings.physical_path -ne (Join-Path $root 'iis/laplace-api') -or
        $settings.hostname -ne 'dev.example.test' -or $settings.port -ne 443) { throw 'IIS installation does not follow the declared root and HTTPS origin.' }
    $env:LAPLACE_IIS_API=Join-Path $work 'explicit site'
    $env:LAPLACE_IIS_SITE='LaplaceDev'; $env:LAPLACE_IIS_POOL='LaplaceDevPool'
    $settings=(& pwsh -NoProfile -File "$repo/deploy/windows/site-settings.ps1" | ConvertFrom-Json)
    if ($LASTEXITCODE -or $settings.physical_path -ne $env:LAPLACE_IIS_API -or $settings.site -ne 'LaplaceDev' -or $settings.pool -ne 'LaplaceDevPool') { throw 'Explicit IIS selection was ignored.' }
    foreach($origin in @('', 'http://server.example.test', 'https://example.test/path', 'https://user@example.test')) {
        $env:LAPLACE_PUBLIC_BASE_URL=$origin
        $null=& pwsh -NoProfile -File "$repo/deploy/windows/site-settings.ps1" 2>&1
        if ($LASTEXITCODE -eq 0) { throw 'An absent or invalid browser origin was accepted.' }
    }
    Write-Output 'IIS configuration: alternate root, explicit selection and invalid-origin refusal passed. Installed IIS is not exercised.'
} finally { foreach($name in $names) { [Environment]::SetEnvironmentVariable($name,$saved[$name]) } }
