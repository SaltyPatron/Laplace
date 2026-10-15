#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Work,[Parameter(Mandatory)][string]$NpgsqlAssembly)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Join-Path ([IO.Path]::GetFullPath($Work)) ('client-connection-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$project=Join-Path $root 'probe.csproj'
if(-not [IO.Path]::IsPathFullyQualified($NpgsqlAssembly) -or -not(Test-Path -LiteralPath $NpgsqlAssembly -PathType Leaf)){throw 'Select the Npgsql assembly from the intended application package.'}
$reference=[Security.SecurityElement]::Escape($NpgsqlAssembly)
[IO.File]::WriteAllText($project,('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>Laplace.Cli</AssemblyName></PropertyGroup><ItemGroup><Reference Include="Npgsql"><HintPath>'+$reference+'</HintPath></Reference></ItemGroup></Project>'))
[IO.File]::WriteAllText((Join-Path $root 'Program.cs'),@'
using System;
using Npgsql;
if (args.Length > 0 && args[0] == "--api-client") {
    if (args.Length != 3 || args[1] != "connection-probe" || args[2] != "argument with spaces") return 21;
    if (Environment.GetEnvironmentVariable("LAPLACE_API_URL") != "https://example.test/") return 22;
    if (!System.IO.File.Exists(Environment.GetEnvironmentVariable("LAPLACE_API_KEY_FILE"))) return 23;
    foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        if (((string)entry.Key).StartsWith("PG", StringComparison.OrdinalIgnoreCase)
            || (string)entry.Key is "LAPLACE_DB" or "LAPLACE_APP_DB" or "LAPLACE_BUILD_ROOT") return 24;
    Console.WriteLine("api-client-process-contract-passed");
    return 0;
}
var connection = new NpgsqlConnectionStringBuilder();
connection.ConnectionString = Environment.GetEnvironmentVariable("LAPLACE_DB");
if (!String.IsNullOrEmpty(connection.Password)) return 11;
if (!String.Equals(connection.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return 12;
if (connection.Database != "laplace-mono" || connection.Username != "laplace_desktop_db") return 13;
if (connection.SslMode != SslMode.VerifyFull || !System.IO.File.Exists(connection.RootCertificate)) return 14;
foreach (var name in new[]{"LAPLACE_APP_DB", "PGPASSWORD", "PGPASSFILE", "PGREQUIREAUTH", "PGHOSTADDR", "PGSERVICE", "LAPLACE_BUILD_ROOT"})
    if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) return 15;
if (args.Length != 2 || args[0] != "connection-probe" || args[1] != "argument with spaces") return 16;
Console.WriteLine("child-process-contract-passed");
return 0;
'@)
$runtime=Join-Path $root 'runtime'
& dotnet build $project -o $runtime --nologo --verbosity quiet
if($LASTEXITCODE){throw 'Could not build the child-process connection probe.'}
$client=Join-Path $root 'client'
$null=New-Item -ItemType Directory -Path $client
Copy-Item -LiteralPath "$PSScriptRoot/../deploy/client/laplace-mono.ps1","$PSScriptRoot/../deploy/windows/database-connection.ps1" -Destination $client
$trust=Join-Path $root 'root.crt'
[IO.File]::WriteAllText($trust,'connection-construction-fixture')
$profile=@{runtime=@{kind='directory';directory=$runtime};database=@{authentication='sspi';host=$env:COMPUTERNAME;port=5432;database='laplace-mono';role='laplace_desktop_db';pool_max=4;root_certificate=$trust};environment=@{}}
$config=@{version=1;default_profile='development';profiles=@{development=$profile}}
$configFile=Join-Path $client 'client.json'
function Invoke-Client {
    [IO.File]::WriteAllText($configFile,($config | ConvertTo-Json -Depth 8))
    $info=[Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    foreach($value in @('-NoProfile','-File',(Join-Path $client 'laplace-mono.ps1'),'connection-probe','argument with spaces')){$info.ArgumentList.Add($value)}
    $info.Environment['LAPLACE_MONO_CLIENT_CONFIG']=$configFile
    $info.Environment['LAPLACE_MONO_PROFILE']='development'
    foreach($name in 'LAPLACE_DB','LAPLACE_APP_DB','PGPASSWORD','PGPASSFILE','PGREQUIREAUTH','PGHOSTADDR','PGSERVICE','LAPLACE_BUILD_ROOT'){$info.Environment[$name]='ambient-tripwire'}
    $process=[Diagnostics.Process]::Start($info)
    try{
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(30000)){$process.Kill($true);throw 'Installed launcher probe timed out.'}
        [pscustomobject]@{Code=$process.ExitCode;Output=$stdout.GetAwaiter().GetResult();Error=$stderr.GetAwaiter().GetResult()}
    }finally{$process.Dispose()}
}
$result=Invoke-Client
if($result.Code -ne 0 -or $result.Output.Trim() -ne 'child-process-contract-passed'){throw "SSPI launcher probe failed: $($result.Code) $($result.Error)"}
$profile.database.host='hart-server'
$result=Invoke-Client
if($result.Code -eq 0 -or $result.Error -notmatch 'requires this machine'){throw 'The launcher did not reject remote SSPI before credential access.'}
$profile.database.host=$env:COMPUTERNAME;$profile.database.authentication='trust'
$result=Invoke-Client
if($result.Code -eq 0 -or $result.Error -notmatch 'Unsupported PostgreSQL authentication'){throw 'The launcher did not reject unsupported authentication before credential access.'}
$keyFile=Join-Path $root 'api-key'
[IO.File]::WriteAllText($keyFile,'fixture-api-key')
$config.version=2
$profile=@{runtime=@{kind='directory';directory=$runtime};api=@{origin='https://example.test';key_file=$keyFile}}
$config.profiles.development=$profile
$result=Invoke-Client
if($result.Code -ne 0 -or $result.Output.Trim() -ne 'api-client-process-contract-passed'){throw "API launcher probe failed: $($result.Code) $($result.Error)"}
$profile.environment=@{LAPLACE_DB='profile-tripwire';PGPASSWORD='profile-tripwire';PGHOST='profile-tripwire'}
$result=Invoke-Client
if($result.Code -ne 0 -or $result.Output.Trim() -ne 'api-client-process-contract-passed'){throw 'API profile environment leaked database access into the child.'}
$profile.database=@{}
$result=Invoke-Client
if($result.Code -eq 0 -or $result.Error -notmatch 'must not include database'){throw 'API profile accepted database targets.'}
$profile.Remove('database')
$profile.api.origin='https://example.test/other'
$result=Invoke-Client
if($result.Code -eq 0 -or $result.Error -notmatch 'HTTPS origin'){throw 'API profile accepted a non-origin credential destination.'}
$installed=Join-Path $root 'installed'
$commands=Join-Path $root 'commands'
$null=New-Item -ItemType Directory -Path $installed,$commands
$installedLauncher=Join-Path $installed 'laplace-mono.ps1'
$installedConfiguration=Join-Path $installed 'client.json'
$command=Join-Path $commands 'laplace-mono.cmd'
[IO.File]::WriteAllText($installedLauncher,'original launcher')
[IO.File]::WriteAllText($installedConfiguration,'original configuration')
[IO.File]::WriteAllText($command,'another command')
$rejected=$false
try { & "$PSScriptRoot/../deploy/client/Install-LaplaceClient.ps1" -Configuration $configFile -Destination $installed -CommandDirectory $commands }
catch { if($_.Exception.Message -notmatch 'Another command already owns'){throw}; $rejected=$true }
if(-not $rejected -or [IO.File]::ReadAllText($installedLauncher) -cne 'original launcher' -or [IO.File]::ReadAllText($installedConfiguration) -cne 'original configuration' -or [IO.File]::ReadAllText($command) -cne 'another command'){throw 'Installer command conflict did not preserve the existing installation before preflight.'}
'Installed launcher process checks passed: Windows authentication needs no credential enrollment, clears ambient credentials/build paths, preserves arguments, and rejects remote or unsupported selections before credential access. No live database was contacted.'
