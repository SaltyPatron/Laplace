# Real Windows file-lock acceptance for the deployment routines. No services,
# databases, installed libraries or source files are changed.
param([string]$SourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')))
$ErrorActionPreference='Stop'
if (-not $IsWindows) { throw 'This acceptance exercises Windows CMD and sharing modes.' }
$temporary=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')
$root=Join-Path $temporary ('laplace-runtime-copy-'+[guid]::NewGuid().ToString('N'))
$savedTemp=$env:TEMP
try {
    $destination=Join-Path $root 'destination'
    $null=New-Item -ItemType Directory -Path $destination -Force
    $env:TEMP=Join-Path $root 'stale'
    $null=New-Item -ItemType Directory -Path $env:TEMP
    $source=Join-Path $root 'input.dll'; $target=Join-Path $destination 'input.dll'
    foreach ($case in @(@{file='engine-runtime.cmd';label='put'},@{file='install-extensions.cmd';label='swapcopy'})) {
        $text=[IO.File]::ReadAllText((Join-Path $SourceRoot ('scripts/win/'+$case.file)))
        $match=[regex]::Match($text,'(?m)^:'+ $case.label +'\r?$')
        if (-not $match.Success) { throw "Missing deployment routine: $($case.label)" }
        $routine=$text.Substring($match.Index)
        $harness=Join-Path $root ($case.label+'.cmd')
        $program="@echo off`r`nsetlocal EnableDelayedExpansion`r`nset `"RUNTIME_CHANGED=0`"`r`nset `"DEST=$destination`"`r`ncall :$($case.label) `"$source`" `"$destination`"`r`nif errorlevel 1 exit /b 1`r`necho changed=!RUNTIME_CHANGED!`r`nexit /b 0`r`n"+$routine
        [IO.File]::WriteAllText($harness,$program,[Text.Encoding]::ASCII)
        [IO.File]::WriteAllText($source,'original'); [IO.File]::WriteAllText($target,'original')
        $time=(Get-Item -LiteralPath $target).LastWriteTimeUtc.Ticks
        $locked=[IO.File]::Open($target,'Open','Read','Read')
        try {
            $result=@(& $env:ComSpec /d /c ('"'+$harness+'"') 2>&1)
            if ($LASTEXITCODE -or $result -notcontains 'changed=0') { throw "Unchanged locked runtime was not reused: $result" }
        } finally { $locked.Dispose() }
        if ((Get-Item -LiteralPath $target).LastWriteTimeUtc.Ticks -ne $time) { throw 'Unchanged runtime timestamp changed.' }
        [IO.File]::WriteAllText($source,'changed')
        $result=@(& $env:ComSpec /d /c ('"'+$harness+'"') 2>&1)
        if ($LASTEXITCODE -or $result -notcontains 'changed=1' -or [IO.File]::ReadAllText($target) -ne 'changed') {
            throw "Changed runtime was not copied and reported: $result"
        }
        Write-Host "$($case.file): locked unchanged file reused; changed file copied and reported."
    }
} finally {
    $env:TEMP=$savedTemp
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -ne $temporary) { throw 'Test cleanup escaped its scratch parent.' }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
