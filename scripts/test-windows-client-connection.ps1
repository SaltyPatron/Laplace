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
var connection = new NpgsqlConnectionStringBuilder();
connection.ConnectionString = Environment.GetEnvironmentVariable("LAPLACE_DB");
if (!String.IsNullOrEmpty(connection.Password)) return 11;
if (!String.Equals(connection.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return 12;
if (connection.Database != "laplace-mono" || connection.Username != "laplace_desktop_db") return 13;
if (connection.SslMode != SslMode.VerifyFull || !System.IO.File.Exists(connection.RootCertificate)) return 14;
foreach (var name in new[]{"LAPLACE_APP_DB", "PGPASSWORD", "PGPASSFILE", "LAPLACE_BUILD_ROOT"})
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
    foreach($name in 'LAPLACE_DB','LAPLACE_APP_DB','PGPASSWORD','PGPASSFILE','LAPLACE_BUILD_ROOT'){$info.Environment[$name]='ambient-tripwire'}
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
'Installed launcher process checks passed: Windows authentication needs no credential enrollment, clears ambient credentials/build paths, preserves arguments, and rejects remote or unsupported selections before credential access. No live database was contacted.'
