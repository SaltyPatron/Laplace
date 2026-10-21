#Requires -Version 7
param(
    [Parameter(Mandatory)][string]$ApiAssembly,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference='Stop'
$assembly=(Resolve-Path -LiteralPath $ApiAssembly).Path
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $evidence){throw 'Choose a new evidence directory; existing evidence is preserved.'}
$null=New-Item -ItemType Directory -Path $evidence

function Start-TestApi([string]$Name,[string]$LogDirectory){
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start()
    $port=$listener.LocalEndpoint.Port
    $listener.Stop()
    $start=[Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.ArgumentList.Add($assembly)
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.WindowStyle='Hidden'
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $start.WorkingDirectory=$evidence
    $start.Environment['LAPLACE_API_PORT']=[string]$port
    $start.Environment['LAPLACE_OPS_LOG_DIR']=$LogDirectory
    $start.Environment['LAPLACE_AUTH_MODE']='header'
    $start.Environment['LAPLACE_BILLING_STORE']='memory'
    $start.Environment['LAPLACE_SKIP_HOSTED_SERVICES']='1'
    # No serving database or external integration is a test prerequisite.
    $start.Environment['LAPLACE_DB']='Host=127.0.0.1;Port=1;Username=validation;Database=not_serving;Timeout=1;SSL Mode=Disable'
    $start.Environment['LAPLACE_APP_DB']=$start.Environment['LAPLACE_DB']
    $process=[Diagnostics.Process]::Start($start)
    return @{Process=$process;Output=$process.StandardOutput.ReadToEndAsync();Error=$process.StandardError.ReadToEndAsync();Name=$Name;Port=$port}
}
function Stop-TestApi($Run){
    try{
        if(-not $Run.Process.HasExited){
            $Run.Process.Kill($true)
            if(-not $Run.Process.WaitForExit(5000)){throw 'Owned test API did not stop.'}
        }
        [IO.File]::WriteAllText((Join-Path $evidence ($Run.Name+'.stdout.log')),$Run.Output.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $evidence ($Run.Name+'.stderr.log')),$Run.Error.GetAwaiter().GetResult())
    }finally{$Run.Process.Dispose()}
}

$logs=Join-Path $evidence 'logs'
$run=Start-TestApi 'healthy' $logs
$handler=[Net.Http.HttpClientHandler]::new()
$handler.UseProxy=$false
$client=[Net.Http.HttpClient]::new($handler)
$client.Timeout=[TimeSpan]::FromSeconds(3)
$client.BaseAddress=[Uri]::new("http://localhost:$($run.Port)")
$marker='logging-acceptance-'+[Guid]::NewGuid().ToString('N')
try{
    $ready=$false
    for($attempt=0;$attempt -lt 100;$attempt++){
        if($run.Process.HasExited){throw 'Test API exited before accepting requests; see its captured stderr.'}
        try{
            $response=$client.GetAsync('/health').GetAwaiter().GetResult()
            try{$ready=$response.IsSuccessStatusCode}finally{$response.Dispose()}
        }catch [Net.Http.HttpRequestException]{}
        if($ready){break}
        Start-Sleep -Milliseconds 100
    }
    if(-not $ready){throw 'Test API did not become reachable.'}
    $client.DefaultRequestHeaders.Add('X-Correlation-Id',$marker)
    $client.DefaultRequestHeaders.Add('Origin',$client.BaseAddress.GetLeftPart([UriPartial]::Authority))
    $body=@{kind='acceptance-check';operation='persistent-logging';path='/example?credential=do-not-retain';requestId=$marker;message=$marker} | ConvertTo-Json -Compress
    $content=[Net.Http.StringContent]::new($body,[Text.Encoding]::UTF8,'application/json')
    try{
        $response=$client.PostAsync('/diagnostics/client',$content).GetAwaiter().GetResult()
        try{if([int]$response.StatusCode -ne 204){throw "Diagnostic acknowledgement returned $([int]$response.StatusCode)."}}finally{$response.Dispose()}
    }finally{$content.Dispose()}
    $response=$client.GetAsync('/health/status').GetAwaiter().GetResult()
    try{
        $status=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        if(-not $status.logging_ready -or $status.logging_failure_count -ne 0){throw 'Healthy persistent sinks were not reported ready.'}
        if($status.substrate_reachable){throw 'The isolated test unexpectedly reached a substrate.'}
    }finally{$response.Dispose()}
}finally{
    $client.Dispose()
    Stop-TestApi $run
}
$events=@(Get-ChildItem -LiteralPath $logs -Filter 'laplace-api-*.jsonl' | ForEach-Object {
    foreach($line in [IO.File]::ReadLines($_.FullName)){$line | ConvertFrom-Json}
})
$event=@($events | Where-Object {$_.Properties.ClientMessage -eq $marker})
if($event.Count -ne 1 -or $event[0].Properties.CorrelationId -ne $marker -or $event[0].Properties.FailedPath -ne '/example'){
    throw 'Diagnostic persistence, request correlation or path sanitization failed.'
}
if(-not @($events | Where-Object {$_.MessageTemplate -eq 'Readiness failed while reading substrate inventory.' -and $_.Exception -and $_.Properties.CorrelationId -eq $marker}).Count){
    throw 'The failed substrate readiness probe lost its exception/correlation diagnostic.'
}
if(-not [IO.File]::ReadAllText((Join-Path $logs 'laplace-api.csv')).Contains($marker)){
    throw 'The SQL-compatible CSV source lost the diagnostic.'
}
foreach($kind in 'csv','json'){
    $blocked=Join-Path $evidence ('blocked-'+$kind)
    $filename=if($kind -eq 'csv'){'laplace-api.csv'}else{'laplace-api-'+(Get-Date -Format yyyyMMdd)+'.jsonl'}
    $null=New-Item -ItemType Directory -Path (Join-Path $blocked $filename)
    $run=Start-TestApi ('blocked-'+$kind) $blocked
    try{
        if(-not $run.Process.WaitForExit(10000)){throw "API accepted inaccessible $kind logging instead of exiting."}
        if($run.Process.ExitCode -ne 1){throw "Logging startup failure returned $($run.Process.ExitCode), expected 1."}
    }finally{Stop-TestApi $run}
    $errorText=[IO.File]::ReadAllText((Join-Path $evidence ('blocked-'+$kind+'.stderr.log')))
    if(-not $errorText.Contains('Laplace API startup/runtime failure:') -or -not $errorText.Contains($filename)){
        throw "Logging startup failure lost the $kind exception/path diagnostic."
    }
}
@{schema='laplace.persistent-logging-acceptance/v1';api_assembly=$assembly;checked_utc=[DateTimeOffset]::UtcNow.ToString('o');marker=$marker;http_diagnostic_persisted=$true;readiness_failure_logged=$true;correlation_preserved=$true;query_string_excluded=$true;csv_verified=$true;startup_csv_failure_exit=1;startup_json_failure_exit=1} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'receipt.json')
Write-Host 'PASS real API acknowledgement, correlated JSONL/CSV persistence, readiness, and controlled startup failures.'
