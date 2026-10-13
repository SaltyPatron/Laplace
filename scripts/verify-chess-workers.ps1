#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ApiBase,
    [Parameter(Mandatory)][string[]]$Profiles,
    [string[]]$Conductors=@('fastchess','cutechess'),
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
if(-not $env:LAPLACE_API_KEY){throw 'Supply an authenticated workload LAPLACE_API_KEY.'}
$base=$ApiBase.TrimEnd('/');$headers=@{Authorization=('Bearer '+$env:LAPLACE_API_KEY)}
$null=New-Item -ItemType Directory -Path $OutputDirectory -Force
function Request([string]$Path,$Body=$null){
    $params=@{Uri=($base+$Path);Headers=$headers;NoProxy=$true;TimeoutSec=30}
    if($null -ne $Body){$params.Method='Post';$params.ContentType='application/json';$params.Body=$Body|ConvertTo-Json -Depth 12 -Compress}
    Invoke-RestMethod @params
}
$catalog=Request '/chess/lab/catalog'
foreach($profile in $Profiles){
    if(-not @($catalog.connections|Where-Object {$_.id -eq $profile -and $_.implementation -eq 'laplace-mono'}).Count){
        throw "The installed API has no declared monorepo worker $profile."
    }
}
$results=@();$jobId=$null
try {
    foreach($profile in $Profiles){
        foreach($conductor in $Conductors){
            $configuration=@{conductor=$conductor;laplaceProfile=$profile;rounds='2';depth='1';concurrency='1';
                ingest='true';stockfishThreads='1';stockfishHashMb='16';limitStrength='false'}
            $preview=Request ('/chess/lab/cutechess/preview?'+(($configuration.GetEnumerator()|ForEach-Object {
                [Uri]::EscapeDataString($_.Key)+'='+[Uri]::EscapeDataString($_.Value)}) -join '&'))
            if(-not $preview.ready){throw "$conductor is not ready for $profile."}
            $jobId=(Request '/chess/lab/start' @{kind='cutechess';config=$configuration}).jobId
            Write-Host "$conductor running $jobId on $profile"
            $deadline=[DateTime]::UtcNow.AddMinutes(8)
            do {
                $job=Request ('/chess/lab/jobs/'+$jobId)
                if($job.state -in @('Completed','Failed','Cancelled')){break}
                if([DateTime]::UtcNow -gt $deadline){throw 'Match exceeded its verification observation window.'}
                Start-Sleep -Seconds 2
            } while($true)
            $transcript=Invoke-WebRequest ($base+'/chess/lab/jobs/'+$jobId+'/terminal.txt') -Headers $headers -NoProxy -TimeoutSec 30
            [IO.File]::WriteAllText((Join-Path $OutputDirectory ($jobId+'.log')),$transcript.Content)
            if($job.state -ne 'Completed'){throw "$conductor/$profile failed: $($job.summary.message)"}
            $recording=Request ('/chess/lab/jobs/'+$jobId+'/artifact/recording.json')
            $experiment=Request ('/chess/lab/jobs/'+$jobId+'/artifact/experiment.json')
            if($recording.status -ne 'completed' -or $recording.readbackGames -ne 2 -or $recording.readbackPlies -le 0){throw 'Persisted games did not read back.'}
            if(-not $job.requester -or $experiment.requester.subject -ne $job.requester.subject -or
                $experiment.requester.tenantId -ne $job.requester.tenantId){throw 'Recorded requester differs from the authenticated job.'}
            if($experiment.requestedOptions.laplaceConnection.id -ne $profile){throw 'Recorded connection differs from the selected worker.'}
            $reported=@($experiment.uciConfiguration.Laplace|Where-Object {$_ -match 'info string laplace knowledge '})
            if(-not $reported.Count){throw 'Remote Laplace did not report its actual knowledge identity.'}
            $results+=@{profile=$profile;conductor=$conductor;job=$job;recording=$recording;experiment=$experiment}
            [IO.File]::WriteAllText((Join-Path $OutputDirectory ($jobId+'.json')),($results[-1]|ConvertTo-Json -Depth 40))
            Write-Host "$conductor/$profile recorded $($recording.readbackGames) games / $($recording.readbackPlies) plies"
            $jobId=$null
        }
    }
    $profile=$Profiles[0]
    $jobId=(Request '/chess/lab/start' @{kind='cutechess';config=@{conductor=$Conductors[0];laplaceProfile=$profile;
        rounds='100';depth='20';concurrency='1';ingest='true';stockfishThreads='1';stockfishHashMb='16';limitStrength='false'}}).jobId
    $deadline=[DateTime]::UtcNow.AddSeconds(40)
    do {
        $text=Invoke-WebRequest ($base+'/chess/lab/jobs/'+$jobId+'/terminal.txt') -Headers $headers -NoProxy -TimeoutSec 10
        if($text.Content -match 'info string laplace knowledge '){break}
        if([DateTime]::UtcNow -gt $deadline){throw 'No remote engine session appeared before cancellation.'}
        Start-Sleep -Seconds 1
    } while($true)
    $null=Request ('/chess/lab/stop/'+$jobId) @{}
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    do {
        $cancelled=Request ('/chess/lab/jobs/'+$jobId)
        if($cancelled.state -eq 'Cancelled'){break}
        if([DateTime]::UtcNow -gt $deadline){throw 'Cancellation did not complete.'}
        Start-Sleep -Seconds 1
    } while($true)
    $jobId=$null
    $rejected=Invoke-WebRequest ($base+'/chess/lab/start') -Method Post -ContentType application/json -Headers $headers -NoProxy -SkipHttpErrorCheck -TimeoutSec 20 `
        -Body '{"kind":"cutechess","config":{"laplaceProfile":"unregistered-worker"}}'
    if([int]$rejected.StatusCode -ne 400){throw 'An undeclared worker was not rejected.'}
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'receipt.json'),(@{verified_utc=[DateTime]::UtcNow.ToString('o');
        api=$base;matches=$results;cancelled=$cancelled;undeclared_worker_status=[int]$rejected.StatusCode}|ConvertTo-Json -Depth 45))
    Write-Host "Cross-host matches, recorded readback and cancellation passed: $OutputDirectory"
} finally {
    if($jobId){$null=Request ('/chess/lab/stop/'+$jobId) @{}}
}
