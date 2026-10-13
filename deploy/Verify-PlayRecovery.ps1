#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$Origin,
    [Parameter(Mandatory)][string]$ApiKey,
    [Parameter(Mandatory)][string]$OtherApiKey,
    [Parameter(Mandatory)][scriptblock]$RestartService,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference='Stop'
function Request([string]$Route,$Body=$null,[string]$Key=$ApiKey) {
    $arguments=@{Uri=([uri]::new($Origin,$Route));Headers=@{Authorization='Bearer '+$Key};NoProxy=$true;TimeoutSec=180}
    if($null -ne $Body){$arguments.Method='Post';$arguments.ContentType='application/json';$arguments.Body=$Body|ConvertTo-Json -Depth 8 -Compress}
    Invoke-RestMethod @arguments
}
function Reject([string]$Route,$Body,[string]$Key,[int]$Status) {
    try { $null=Request $Route $Body $Key; throw "Unexpected success for $Route" }
    catch { if([int]$_.Exception.Response.StatusCode -ne $Status){throw} }
}
function Restart {
    & $RestartService
    for($attempt=0;$attempt -lt 30;$attempt++){
        try{$null=Request '/health';return}catch{if($attempt -eq 29){throw};Start-Sleep -Seconds 1}
    }
}
$start=Request '/chess/play/start' @{record=$true;moves=@('f2f3','e7e5')}
$id=$start.sessionId
$third=Request '/chess/play/move' @{sessionId=$id;fen=$start.fen;uci='g2g4'}
if(-not $third.legal -or $third.ply -ne 3){throw 'Opening moves were not accepted.'}
$before=Request ('/chess/play/'+$id)
Restart
Reject ('/chess/play/'+$id) $null $OtherApiKey 404
Reject '/chess/play/bestmove' @{sessionId=$id;fen=$third.fen;depth=2;substrate=$true} $OtherApiKey 404
Reject '/chess/play/move' @{sessionId=$id;fen=$start.fen;uci='d8h4'} $ApiKey 409
$resumed=Request ('/chess/play/'+$id)
if(($before.moves -join ' ') -ne ($resumed.moves -join ' ') -or
   $before.requester.subject -ne $resumed.requester.subject -or $resumed.positionFens.Count -ne 4){throw 'Restart changed the retained operation.'}
$engine=Request '/chess/play/bestmove' @{sessionId=$id;fen=$third.fen;depth=2;substrate=$true}
if($engine.uci -ne 'd8h4' -or -not $engine.terminal){throw 'The installed engine did not complete the mating line.'}
Restart
$completed=Request ('/chess/play/'+$id)
if($completed.state -ne 'completed' -or $completed.recording -ne 'recorded' -or
   $completed.moves.Count -ne 4 -or -not $completed.recordedPlayingId){throw 'Completed recording did not survive restart.'}
$game=Request ('/v1/chess/games/'+$completed.recordedPlayingId)
$plies=Request ('/v1/chess/games/'+$completed.recordedPlayingId+'/plies')
if($game.result -ne '0-1'){throw 'Persisted game result disagrees with the actual game.'}
if($plies.truncated -or $plies.plies.Count -ne 4 -or ($plies.plies.uci -join ' ') -ne ($completed.moves -join ' ') -or
   $plies.plies[-1].fen -ne $completed.fen){throw 'Persisted move trajectory does not replay exactly.'}
$result=@{verified_utc=[DateTime]::UtcNow.ToString('o');origin=$Origin.AbsoluteUri;session=$id;
    restarts=2;cross_requester_denials=2;stale_request_rejected=$true;operation=$completed;game=$game;plies=$plies}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Output)))|Out-Null
[IO.File]::WriteAllText($Output,($result|ConvertTo-Json -Depth 20))
Write-Host "Installed game resumed, reached checkmate, and read back through the authenticated API: $Output"
