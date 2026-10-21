#Requires -Version 7
param(
    [Parameter(Mandatory)][string]$ManagedBinRoot,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference='Stop'
$binaryRoot=(Resolve-Path -LiteralPath $ManagedBinRoot).Path
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $evidence){throw 'Choose a new evidence directory; existing evidence is preserved.'}
$null=New-Item -ItemType Directory -Path $evidence

$commands=@(
    @{Role='cli';Project='Laplace.Cli';Assembly='Laplace.Cli.dll';Arguments=@('--api-client','--help');Input='';ExpectedOutput='laplace-mono ops'},
    @{Role='mcp';Project='Laplace.Endpoints.Mcp';Assembly='Laplace.Endpoints.Mcp.dll';Arguments=@();Input='';ExpectedOutput=$null},
    @{Role='migrations';Project='Laplace.Migrations';Assembly='Laplace.Migrations.dll';Arguments=@('--help');Input='';ExpectedOutput='USAGE:'},
    @{Role='uci';Project='Laplace.Chess.Uci';Assembly='laplace-uci.dll';Arguments=@();Input="uci`nquit`n";ExpectedOutput='uciok'}
)
$results=@()
foreach($command in $commands){
    $assembly=(Resolve-Path -LiteralPath (Join-Path $binaryRoot ($command.Project+'/Release/net10.0/'+$command.Assembly))).Path
    foreach($mode in 'healthy','blocked-csv','blocked-json'){
        $name=$command.Role+'-'+$mode
        $logs=Join-Path $evidence ($name+'-logs')
        if($mode -ne 'healthy'){
            $filename=if($mode -eq 'blocked-csv'){'laplace-'+$command.Role+'.csv'}else{'laplace-'+$command.Role+'-'+(Get-Date -Format yyyyMMdd)+'.jsonl'}
            $null=New-Item -ItemType Directory -Path (Join-Path $logs $filename)
        }
        $start=[Diagnostics.ProcessStartInfo]::new('dotnet')
        $start.ArgumentList.Add($assembly)
        foreach($argument in $command.Arguments){$start.ArgumentList.Add($argument)}
        $start.UseShellExecute=$false
        $start.CreateNoWindow=$true
        $start.WindowStyle='Hidden'
        $start.RedirectStandardInput=$true
        $start.RedirectStandardOutput=$true
        $start.RedirectStandardError=$true
        $start.WorkingDirectory=$evidence
        $start.Environment['LAPLACE_OPS_LOG_DIR']=$logs
        $start.Environment['LAPLACE_DB']='Host=127.0.0.1;Port=1;Username=validation;Database=not_serving;Timeout=1;SSL Mode=Disable'
        $start.Environment['LAPLACE_APP_DB']=$start.Environment['LAPLACE_DB']
        $process=[Diagnostics.Process]::Start($start)
        try{
            $stdoutTask=$process.StandardOutput.ReadToEndAsync()
            $stderrTask=$process.StandardError.ReadToEndAsync()
            $process.StandardInput.Write($command.Input)
            $process.StandardInput.Close()
            if(-not $process.WaitForExit(15000)){throw "$name did not finish within 15 seconds."}
            $stdout=$stdoutTask.GetAwaiter().GetResult()
            $stderr=$stderrTask.GetAwaiter().GetResult()
            [IO.File]::WriteAllText((Join-Path $evidence ($name+'.stdout.log')),$stdout)
            [IO.File]::WriteAllText((Join-Path $evidence ($name+'.stderr.log')),$stderr)
            $expected=if($mode -eq 'healthy'){0}else{1}
            if($process.ExitCode -ne $expected){throw "$name returned $($process.ExitCode), expected $expected; see captured output."}
            if($mode -eq 'healthy'){
                if($command.ExpectedOutput -and -not $stdout.Contains($command.ExpectedOutput,[StringComparison]::OrdinalIgnoreCase)){
                    throw "$name lost its command/protocol response."
                }
                if(-not (Test-Path -LiteralPath (Join-Path $logs ('laplace-'+$command.Role+'.csv')))){
                    throw "$name did not initialize persistent CSV logging."
                }
                if($stderr.Contains('LOGGING_UNAVAILABLE') -or $stderr.Contains('LOGGING_FAILURE')){throw "$name reported unhealthy logging."}
            }else{
                if(-not $stderr.Contains($filename) -or -not $stderr.Contains('LOGGING_UNAVAILABLE')){
                    throw "$name lost the failed log path or terminal logging diagnostic."
                }
                if($stdout.Contains('LOGGING_') -or $stdout.Contains('Exception')){throw "$name contaminated command/protocol stdout."}
            }
            $results+=@{role=$command.Role;mode=$mode;exit_code=$process.ExitCode;passed=$true}
        }finally{
            try{
                if(-not $process.HasExited){
                    $process.Kill($true)
                    if(-not $process.WaitForExit(5000)){throw "Owned $name process did not stop."}
                }
            }finally{$process.Dispose()}
        }
    }
}
@{schema='laplace.process-logging-acceptance/v1';checked_utc=[DateTimeOffset]::UtcNow.ToString('o');managed_bin_root=$binaryRoot;checks=$results} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'receipt.json')
Write-Host 'PASS real CLI, MCP, migrations and UCI process exits, protocol output, and controlled CSV/JSONL startup failures.'
