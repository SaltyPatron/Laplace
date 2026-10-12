#Requires -Version 7
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not(Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs=(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
    if(-not $vs){throw 'MSVC is required to exercise the actual native catalog.'}
    $vcvars=Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
    $lines=& cmd /d /c "`"$vcvars`" >nul && set"
    if($LASTEXITCODE){throw 'MSVC initialization failed.'}
    foreach($line in $lines){if($line -match '^([^=]+)=(.*)$'){[Environment]::SetEnvironmentVariable($Matches[1],$Matches[2])}}
}
$scratch=if($env:RUNNER_TEMP){$env:RUNNER_TEMP}elseif($env:LAPLACE_WORK){Join-Path $env:LAPLACE_WORK 'package-checks'}else{throw 'Declare RUNNER_TEMP or LAPLACE_WORK.'}
$work=Join-Path $scratch ('native-package-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $work -Force
foreach($kind in 'current','stale') {
    $directory=Join-Path $work $kind
    $null=New-Item -ItemType Directory -Path $directory
    Copy-Item -LiteralPath (Join-Path $repo 'engine/core/src/sql_catalog.c') -Destination $directory
    $catalog=[IO.File]::ReadAllText((Join-Path $repo 'engine/core/src/sql_catalog.def'))
    if($kind -eq 'stale'){$catalog=$catalog.Replace('pg_catalog.pg_has_role','pg_catalog.pg_has_role_obsolete')}
    [IO.File]::WriteAllText((Join-Path $directory 'sql_catalog.def'),$catalog)
    Push-Location $directory
    try {
        & cl.exe /nologo /LD /utf-8 "/I$repo/engine/core/include" sql_catalog.c /link /OUT:laplace_core.dll /EXPORT:laplace_sql_query_text /EXPORT:laplace_sql_query_parameters
        if($LASTEXITCODE){throw 'Native catalog compilation failed.'}
    } finally {Pop-Location}
}
& "$repo/deploy/windows/Test-NativeSqlCatalog.ps1" -Library "$work/current/laplace_core.dll"
$published=Join-Path $work 'published'
$null=New-Item -ItemType Directory -Path "$published/cli","$published/uci"
Copy-Item "$work/current/laplace_core.dll" $published
Copy-Item "$work/current/laplace_core.dll" "$published/uci"
Copy-Item "$work/stale/laplace_core.dll" "$published/cli"
[IO.File]::WriteAllText("$published/uci/laplace-uci.exe",'unused: catalog rejection must precede package creation')
$destination=Join-Path $work 'package'
$rejected=$false
try {& "$repo/deploy/windows/New-IisPackage.ps1" -Published $published -Destination $destination -Revision ('1'*40)}
catch {
    if($_.Exception.Message -notmatch 'Native SQL contract differs from source: ingest.clean_gin_pending_lists'){throw}
    $rejected=$true
}
if(-not $rejected -or (Test-Path -LiteralPath $destination)){throw 'A stale CLI catalog reached package creation.'}
Write-Host 'PASS: current native catalog accepted; stale CLI catalog rejected before package creation.'
