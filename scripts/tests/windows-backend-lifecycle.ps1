# Run the installer's actual session-lifecycle routine. The forced-maintenance
# branch uses a recording client; optional live acceptance keeps a real query
# running through ordinary publication without changing installed files.
param([string]$ConnectionInfo)
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'This acceptance exercises the Windows installer.'}
$temporary=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')
$root=Join-Path $temporary ('laplace-backend-lifecycle-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$peer=$null
try {
    $installer=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../win/install-extensions.cmd'))
    $routine=[regex]::Match($installer,'(?ms)^:recycle_backends\r?\n.*?(?=^:clients_refreshed\r?$)').Value
    if(-not $routine){throw 'Installer session-lifecycle routine is missing.'}
    $calls=Join-Path $root 'calls.txt'
    $client=Join-Path $root 'record-client.cmd'
    [IO.File]::WriteAllText($client,"@echo invoked>>`"$calls`"`r`n@exit /b 0`r`n")
    if($ConnectionInfo){
        $psql=(Get-Command psql.exe -ErrorAction Stop).Source
        $start=[Diagnostics.ProcessStartInfo]::new($psql)
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach($arg in @('-X','-w','-v','ON_ERROR_STOP=1','--dbname',$ConnectionInfo,'-c',"BEGIN; SELECT pg_sleep(8); ROLLBACK;")){$start.ArgumentList.Add($arg)}
        $peer=[Diagnostics.Process]::Start($start)
        $stdout=$peer.StandardOutput.ReadToEndAsync(); $stderr=$peer.StandardError.ReadToEndAsync()
        Start-Sleep -Milliseconds 500
        if($peer.HasExited){throw "Live acceptance query did not remain connected: $($stderr.GetAwaiter().GetResult())"}
    }
    foreach($case in @(@{recycle=0;force=0;changed=1},@{recycle=1;force=0;changed=1},@{recycle=1;force=0;changed=0},@{recycle=1;force=1;changed=1})){
        $harness=Join-Path $root 'lifecycle.cmd'
        $sql=Join-Path $root 'maintenance.sql'
        $program="@echo off`r`nsetlocal`r`nset `"RECYCLE=$($case.recycle)`"`r`nset `"FORCE_RECYCLE=$($case.force)`"`r`nset `"RUNTIME_CHANGED=$($case.changed)`"`r`nset `"PSQL=$client`"`r`nset `"GUC_SQL=$sql`"`r`ncall :recycle_backends`r`nexit /b %errorlevel%`r`n"+$routine
        [IO.File]::WriteAllText($harness,$program,[Text.Encoding]::ASCII)
        & $harness
        if($LASTEXITCODE){throw 'Installer lifecycle routine failed.'}
        if($case.force -eq 0 -and (Test-Path -LiteralPath $calls)){throw 'Ordinary publication invoked backend termination.'}
        if($case.force -eq 1 -and -not (Test-Path -LiteralPath $calls)){throw 'Explicit force-maintenance did not invoke its database client.'}
    }
    if($peer){
        if(-not $peer.WaitForExit(15000)){throw 'Live acceptance query did not finish.'}
        if($peer.ExitCode -ne 0){throw "Publication interrupted a live database session: $($stderr.GetAwaiter().GetResult())"}
        $null=$stdout.GetAwaiter().GetResult()
        'Live PostgreSQL transaction completed across ordinary publication.'
    }
    'PostgreSQL client lifecycle: ordinary and legacy recycle preserve sessions; only explicit force invokes termination.'
}finally{
    if($peer){if(-not $peer.HasExited){$peer.Kill()};$peer.Dispose()}
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -ne $temporary){throw 'Invalid acceptance cleanup root.'}
    Remove-Item -LiteralPath $root -Recurse -Force
}
