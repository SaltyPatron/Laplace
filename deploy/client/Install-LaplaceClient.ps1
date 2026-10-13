#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Configuration,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$CommandDirectory
)
$ErrorActionPreference='Stop'
foreach ($path in @($Configuration,$Destination,$CommandDirectory)) {
    if (-not [IO.Path]::IsPathFullyQualified($path)) { throw 'Client setup paths must be absolute.' }
}
$config=Get-Content -LiteralPath $Configuration -Raw | ConvertFrom-Json -AsHashtable
if ($config.version -ne 1 -or -not $config.profiles.ContainsKey($config.default_profile)) { throw 'Invalid client declaration/default profile.' }
[void](New-Item -ItemType Directory -Path $Destination,$CommandDirectory -Force)
Copy-Item -LiteralPath "$PSScriptRoot/laplace-mono.ps1" -Destination (Join-Path $Destination 'laplace-mono.ps1') -Force
Copy-Item -LiteralPath "$PSScriptRoot/../windows/database-connection.ps1" -Destination (Join-Path $Destination 'database-connection.ps1') -Force
[IO.File]::WriteAllText((Join-Path $Destination 'client.json'),($config | ConvertTo-Json -Depth 16))
$launcher=Join-Path $Destination 'laplace-mono.ps1'
$pwsh=(Get-Command pwsh -CommandType Application).Source
$command=Join-Path $CommandDirectory 'laplace-mono.cmd'
$body='@echo off'+"`r`n"+'setlocal DisableDelayedExpansion'+"`r`n"+'"'+$pwsh.Replace('%','%%')+'" -NoLogo -NoProfile -File "'+$launcher.Replace('%','%%')+'" %*'+"`r`n"+'exit /b %errorlevel%'+"`r`n"
[IO.File]::WriteAllText($command,$body)
& $pwsh -NoProfile -File $launcher --where
if ($LASTEXITCODE) { throw 'Installed client resolution failed.' }
& $pwsh -NoProfile -File $launcher decompose Laplace
if ($LASTEXITCODE) { throw 'Installed native CLI failed.' }
$userPath=[Environment]::GetEnvironmentVariable('PATH','User')
if ($CommandDirectory.TrimEnd('\') -notin @($userPath.Split(';') | ForEach-Object {$_.TrimEnd('\')})) {
    [Environment]::SetEnvironmentVariable('PATH',($userPath.TrimEnd(';')+';'+$CommandDirectory),'User')
}
if ($CommandDirectory -notin $env:PATH.Split(';')) { $env:PATH=$CommandDirectory+';'+$env:PATH }
Write-Host "Installed laplace-mono: $command"
