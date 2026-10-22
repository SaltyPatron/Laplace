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
# Independent CLI processes share one role journal. Force rollover while every
# archive is retained, then account for each process in strict CSV and JSONL.
$sharedLogs=Join-Path $evidence 'concurrent-cli-logs'
$cli=(Resolve-Path -LiteralPath (Join-Path $binaryRoot 'Laplace.Cli/Release/net10.0/Laplace.Cli.dll')).Path
$ownedIds=@()
foreach($batch in 0..3){
    $owned=@()
    try{
        foreach($index in 0..7){
            $start=[Diagnostics.ProcessStartInfo]::new('dotnet')
            foreach($argument in @($cli,'--api-client','op',"retention-$batch-$index")){$start.ArgumentList.Add($argument)}
            $start.UseShellExecute=$false
            $start.CreateNoWindow=$true
            $start.WindowStyle='Hidden'
            $start.RedirectStandardOutput=$true
            $start.RedirectStandardError=$true
            $start.Environment['LAPLACE_OPS_LOG_DIR']=$sharedLogs
            $start.Environment['LAPLACE_LOG_FILE_BYTES']='8192'
            $start.Environment['LAPLACE_LOG_RETAINED_FILES']=if($batch -eq 3){'3'}else{'1000'}
            # A controlled argument error produces a real typed event without
            # native engine startup, network traffic, or operator credentials.
            $start.Environment['LAPLACE_API_URL']='invalid-validation-origin'
            $start.Environment['LAPLACE_API_KEY_FILE']=''
            $process=[Diagnostics.Process]::Start($start)
            $owned+=@{Process=$process;Out=$process.StandardOutput.ReadToEndAsync();Err=$process.StandardError.ReadToEndAsync()}
        }
        foreach($child in $owned){
            $process=$child.Process
            if(-not $process.WaitForExit(30000)){throw 'Concurrent logging child did not complete within 30 seconds.'}
            $stdout=$child.Out.GetAwaiter().GetResult()
            $stderr=$child.Err.GetAwaiter().GetResult()
            [IO.File]::WriteAllText((Join-Path $evidence ("concurrent-$($process.Id).stdout.log")),$stdout)
            [IO.File]::WriteAllText((Join-Path $evidence ("concurrent-$($process.Id).stderr.log")),$stderr)
            if($process.ExitCode -ne 1 -or $stderr.Contains('LOGGING_') -or $stderr.Contains('CSV_RETENTION_FAILED')){
                throw "Concurrent logging child $($process.Id) lost its expected operation failure or reported a logging failure."
            }
            if($stdout.Length){throw 'Concurrent CLI argument failure contaminated stdout.'}
            if($batch -lt 3){$ownedIds+=$process.Id}
        }
        if($batch -eq 2){
            $csvEvents=@(Get-ChildItem -LiteralPath $sharedLogs -Filter 'laplace-cli*.csv' | ForEach-Object {
                Import-Csv -LiteralPath $_.FullName -Header log_time,application_name,error_severity,category,message,detail | ForEach-Object {
                    if($_.application_name -ne 'cli' -or -not $_.detail){throw 'Malformed concurrent CSV record.'}
                    $_.detail | ConvertFrom-Json
                }
            })
            $jsonEvents=@(Get-ChildItem -LiteralPath $sharedLogs -Filter 'laplace-cli-*.jsonl' | ForEach-Object {
                Get-Content -LiteralPath $_.FullName | ForEach-Object {$_ | ConvertFrom-Json}
            })
            foreach($childId in $ownedIds){
                foreach($events in @(@{Name='CSV';Rows=$csvEvents},@{Name='JSONL';Rows=$jsonEvents})){
                    $failures=@($events.Rows | Where-Object {$_.Properties.ProcessId -eq $childId -and $_.Exception -like '*ArgumentException*'})
                    if($failures.Count -ne 1){throw "$($events.Name) retained $($failures.Count) typed failures for process $childId; expected exactly one."}
                    if(-not $failures[0].Properties.OperationId){throw 'Concurrent event lost its operation correlation.'}
                }
            }
            $results+=@{role='cli';mode='concurrent-rollover';processes=$ownedIds.Count;csv_events=$csvEvents.Count;json_events=$jsonEvents.Count;passed=$true}
        }
    }finally{
        foreach($child in $owned){
            try{
                if(-not $child.Process.HasExited){
                    $child.Process.Kill($true)
                    if(-not $child.Process.WaitForExit(5000)){throw 'Owned concurrent logging child did not stop.'}
                }
            }finally{$child.Process.Dispose()}
        }
    }
}
if(@(Get-ChildItem -LiteralPath $sharedLogs -Filter 'laplace-cli*.csv').Count -ne 3){throw 'CSV retention did not prune to three files.'}
if(@(Get-ChildItem -LiteralPath $sharedLogs -Filter 'laplace-cli-*.jsonl').Count -ne 3){throw 'JSONL retention did not prune to three files.'}
$results+=@{role='cli';mode='retention-prune';retained_files_per_format=3;passed=$true}
@{schema='laplace.process-logging-acceptance/v1';checked_utc=[DateTimeOffset]::UtcNow.ToString('o');managed_bin_root=$binaryRoot;checks=$results} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'receipt.json')
Write-Host 'PASS process exits, protocol output, CSV/JSONL failures, 24 concurrent-writer event accounts, and bounded retention.'
