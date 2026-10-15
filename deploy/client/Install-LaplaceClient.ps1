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
if ($config.version -notin @(1,2) -or -not $config.profiles.ContainsKey($config.default_profile)) { throw 'Invalid client declaration/default profile.' }
$pwsh=(Get-Command pwsh -CommandType Application).Source
$launcher=Join-Path $Destination 'laplace-mono.ps1'
$command=Join-Path $CommandDirectory 'laplace-mono.cmd'
$body='@echo off'+"`r`n"+'setlocal DisableDelayedExpansion'+"`r`n"+'"'+$pwsh.Replace('%','%%')+'" -NoLogo -NoProfile -File "'+$launcher.Replace('%','%%')+'" %*'+"`r`n"+'exit /b %errorlevel%'+"`r`n"
if (Test-Path -LiteralPath $command) {
    if (-not (Test-Path -LiteralPath $command -PathType Leaf) -or [IO.File]::ReadAllText($command) -cne $body) {
        throw "Another command already owns $command; preserved."
    }
}
function Invoke-ClientCheck([string]$Launcher,[string]$ConfigurationPath,[string[]]$CommandArguments) {
    $start=[Diagnostics.ProcessStartInfo]::new($pwsh)
    $start.UseShellExecute=$false
    $start.Environment['LAPLACE_MONO_CLIENT_CONFIG']=$ConfigurationPath
    foreach ($argument in @('-NoLogo','-NoProfile','-File',$Launcher,'--profile',$config.default_profile)+$CommandArguments) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::Start($start)
    try {
        $process.WaitForExit()
        if ($process.ExitCode) { throw "Installed client operation failed with exit code $($process.ExitCode)." }
    } finally { $process.Dispose() }
}
if ($config.version -eq 2) { Invoke-ClientCheck "$PSScriptRoot/laplace-mono.ps1" $Configuration @('ops') }
[void](New-Item -ItemType Directory -Path $Destination,$CommandDirectory -Force)
Copy-Item -LiteralPath "$PSScriptRoot/laplace-mono.ps1" -Destination (Join-Path $Destination 'laplace-mono.ps1') -Force
Copy-Item -LiteralPath "$PSScriptRoot/../windows/database-connection.ps1" -Destination (Join-Path $Destination 'database-connection.ps1') -Force
[IO.File]::WriteAllText((Join-Path $Destination 'client.json'),($config | ConvertTo-Json -Depth 16))
[IO.File]::WriteAllText($command,$body)
Invoke-ClientCheck $launcher (Join-Path $Destination 'client.json') @('--where')
if ($config.version -eq 2) { Invoke-ClientCheck $launcher (Join-Path $Destination 'client.json') @('ops') }
else { Invoke-ClientCheck $launcher (Join-Path $Destination 'client.json') @('decompose','Laplace') }
$userPath=[Environment]::GetEnvironmentVariable('PATH','User')
if ($CommandDirectory.TrimEnd('\') -notin @($userPath.Split(';') | ForEach-Object {$_.TrimEnd('\')})) {
    [Environment]::SetEnvironmentVariable('PATH',($userPath.TrimEnd(';')+';'+$CommandDirectory),'User')
}
if ($CommandDirectory -notin $env:PATH.Split(';')) { $env:PATH=$CommandDirectory+';'+$env:PATH }
Write-Host "Installed laplace-mono: $command"
