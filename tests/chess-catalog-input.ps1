#Requires -Version 7
param(
    [Parameter(Mandatory)][string]$CatalogAssembly,
    [Parameter(Mandatory)][string]$PerfcachePath,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference='Stop'
$assembly=(Resolve-Path -LiteralPath $CatalogAssembly).Path
$perfcache=(Resolve-Path -LiteralPath $PerfcachePath).Path
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $evidence){throw 'Choose a new evidence directory; existing evidence is preserved.'}
$null=New-Item -ItemType Directory -Path $evidence
$checks=@()
$fixtures=@(
    @{Name='none';Input='NONE';Exit=0;Transitions=0},
    @{Name='valid';Row="A00`tFixture`t1. e4 e5";Exit=0;Transitions=2},
    @{Name='illegal-first';Row="A00`tIllegal`t1. e5";Exit=1;Cause="illegal SAN 'e5'"},
    @{Name='illegal-prefix';Row="A00`tIllegal prefix`t1. e4 e5 2. e4";Exit=1;Cause='ply 3'},
    @{Name='malformed-row';Row="A00`tMissing movetext";Exit=1;Cause='columns'},
    @{Name='incomplete-pgn';Row="A00`tRecovered PGN`t1. e4 e5 {unterminated";Exit=1;Cause='syntax'},
    @{Name='empty-directory';Exit=1;Cause='no opening records'},
    @{Name='missing-directory';Missing=$true;Exit=2;Cause='missing openings'},
    @{Name='blocked-output';Row="A00`tFixture`t1. e4 e5";Blocked=$true;Exit=1;Cause='surfaces.txt'}
)
foreach($fixture in $fixtures){
    $root=Join-Path $evidence $fixture.Name
    $inputPath=Join-Path $root 'openings'
    $null=New-Item -ItemType Directory -Path $root
    if(-not $fixture.Missing){$null=New-Item -ItemType Directory -Path $inputPath}
    if($fixture.Row){[IO.File]::WriteAllText((Join-Path $inputPath 'a.tsv'),"eco`tname`tpgn`n"+$fixture.Row+"`n",[Text.UTF8Encoding]::new($false))}
    $surface=Join-Path $root 'surfaces.txt'
    $transition=Join-Path $root 'transitions.bin'
    if($fixture.Blocked){$null=New-Item -ItemType Directory -Path $surface}
    else{[IO.File]::WriteAllText($surface,'previous-verified-surfaces')}
    [IO.File]::WriteAllText($transition,'previous-verified-transitions')
    $start=[Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach($argument in @($assembly,$(if($fixture.Input){$fixture.Input}else{$inputPath}),$surface,$transition)){$start.ArgumentList.Add($argument)}
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.WindowStyle='Hidden'
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $start.Environment['LAPLACE_PERFCACHE_BIN']=$perfcache
    $process=[Diagnostics.Process]::Start($start)
    try{
        $outputTask=$process.StandardOutput.ReadToEndAsync()
        $errorTask=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(45000)){throw 'Owned catalog input fixture timed out.'}
        $stdout=$outputTask.GetAwaiter().GetResult()
        $stderr=$errorTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $root 'stdout.log'),$stdout)
        [IO.File]::WriteAllText((Join-Path $root 'stderr.log'),$stderr)
        if($process.ExitCode -ne $fixture.Exit){throw "$($fixture.Name): exit $($process.ExitCode), expected $($fixture.Exit); see captured diagnostic."}
        if($fixture.Exit -eq 0){
            $match=[regex]::Match($stderr,'chess_catalog: (\d+) surfaces.*; (\d+) transitions')
            if(-not $match.Success -or [int]$match.Groups[2].Value -ne $fixture.Transitions){throw 'Catalog count/readback mismatch.'}
            if([int]$match.Groups[1].Value -lt 960){throw 'Deterministic Chess960 floor was silently shortened.'}
        }else{
            if(-not $stderr.Contains($fixture.Cause,[StringComparison]::OrdinalIgnoreCase)){throw "$($fixture.Name): lost the expected cause."}
            if($fixture.Row -and -not $fixture.Blocked -and -not $stderr.Contains('a.tsv:2')){throw 'Invalid row lost its source path/line.'}
            if($stderr.Contains('chess_catalog:')){throw 'A failed catalog reported success.'}
            if(-not $fixture.Blocked -and [IO.File]::ReadAllText($surface) -ne 'previous-verified-surfaces'){throw 'Failed input replaced the previous surface artifact.'}
            if([IO.File]::ReadAllText($transition) -ne 'previous-verified-transitions'){throw 'Failed input/output replaced the previous transition artifact.'}
        }
        $checks+=@{name=$fixture.Name;exit_code=$process.ExitCode;passed=$true}
    }finally{
        try{
            if(-not $process.HasExited){
                $process.Kill($true)
                if(-not $process.WaitForExit(5000)){throw 'Owned catalog fixture did not stop.'}
            }
        }finally{$process.Dispose()}
    }
}
@{schema='laplace.chess-catalog-input-acceptance/v1';checked_utc=[DateTimeOffset]::UtcNow.ToString('o');assembly=$assembly;checks=$checks} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'receipt.json')
Write-Host 'PASS catalog counts, controlled error exits, source/line causes, and preservation of prior artifacts on invalid input.'
