#Requires -Version 7
param([Parameter(Mandatory)][string]$Work)
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetFullPath($Work)) ('api-client-setup-'+[guid]::NewGuid().ToString('N'))
$client=Join-Path $root 'deploy/client'
[void](New-Item -ItemType Directory -Path $client)
Copy-Item -LiteralPath "$PSScriptRoot/../deploy/client/Install-LaplaceApiClient.ps1" -Destination $client
$key=Join-Path $root 'private key'
[IO.File]::WriteAllText($key,'private-key-tripwire')
[IO.File]::WriteAllText((Join-Path $client 'Install-LaplaceClient.ps1'),@'
param($Configuration,$Destination,$CommandDirectory)
$content=[IO.File]::ReadAllText($Configuration)
if($content.Contains('private-key-tripwire')){throw 'Key content was copied into configuration.'}
$value=$content | ConvertFrom-Json -AsHashtable
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if($value.version -ne 2 -or $value.profiles.service.runtime.directory -ne (Join-Path $root 'runtime') -or
   $value.profiles.service.api.origin -ne 'https://example.test' -or
   $value.profiles.service.api.key_file -ne (Join-Path $root 'private key')){throw 'Invalid packaged profile handoff.'}
[IO.File]::WriteAllText((Join-Path $root 'configuration-path'),$Configuration)
throw 'expected-service-preflight-failure'
'@)
$rejected=$false
try {
    & "$client/Install-LaplaceApiClient.ps1" -Origin 'https://example.test' -KeyFile $key -Destination (Join-Path $root 'installed') -CommandDirectory (Join-Path $root 'commands')
} catch {
    if($_.Exception.Message -ne 'expected-service-preflight-failure'){throw}
    $rejected=$true
}
$configuration=[IO.File]::ReadAllText((Join-Path $root 'configuration-path'))
if(-not $rejected -or (Test-Path -LiteralPath $configuration) -or [IO.File]::ReadAllText($key) -cne 'private-key-tripwire'){
    throw 'Packaged installer failed to preserve the key or clean its temporary configuration.'
}
'Packaged Windows setup passed: runtime selection, private key path, failed preflight propagation and temporary configuration cleanup.'
