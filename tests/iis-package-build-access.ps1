#Requires -Version 7
$ErrorActionPreference='Stop'
$work=Join-Path ([IO.Path]::GetTempPath()) ('iis-package-access-'+[Guid]::NewGuid().ToString('N'))
$deploy=Join-Path $work 'deploy/windows'
$scripts=Join-Path $work 'scripts/win'
$null=[IO.Directory]::CreateDirectory($deploy)
$null=[IO.Directory]::CreateDirectory($scripts)
Copy-Item -LiteralPath "$PSScriptRoot/../deploy/windows/Build-IisPackage.ps1" -Destination $deploy
Set-Content -LiteralPath (Join-Path $scripts 'machine-env.ps1') -Value '# Fixture: no installed host configuration.'
Set-Content -LiteralPath (Join-Path $scripts 'laplace-paths.ps1') -Value 'function Import-LaplaceEnv {}'
Set-Content -LiteralPath (Join-Path $deploy 'iis-runtime.ps1') -Value 'function Read-IisInstallation { throw "fixture: installation read" }'
$names=@('VSCMD_VER','SETVARS_COMPLETED','LAPLACE_IIS_INSTALLATION','LAPLACE_API_KEY','LAPLACE_ENGINE_BUILD','LAPLACE_ROOT',
    'LAPLACE_DEVELOPMENT_INSTALLATION','LAPLACE_OPERATOR_TOKEN','LAPLACE_SRC','LAPLACE_WORK','LAPLACE_BUILD_ROOT',
    'LAPLACE_PGHOST','LAPLACE_PGPORT','LAPLACE_PGUSER','LAPLACE_TEST_CHECK_FILE','PGSERVICE','PGHOSTADDR','PGOPTIONS','PGPASSWORD',
    'PGSSLMODE','PGSSLROOTCERT','PGREQUIREAUTH','PGCONNECT_TIMEOUT','PGBIN')
$saved=@{}
foreach($name in $names){$saved[$name]=[Environment]::GetEnvironmentVariable($name)}
function icx.exe {}
# Stub the current component builder; the production PowerShell entry point still
# performs real source/path selection, publication preflight, and ordering.
$buildResult=73
function python {
    param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
    $global:LASTEXITCODE=$buildResult
    if($buildResult -eq 0){
        $index=[Array]::IndexOf($Arguments,'--output')
        if($index -lt 0){throw 'Component builder did not receive its output receipt.'}
        Set-Content -LiteralPath $Arguments[$index+1] -Value '{"components":[]}'
    }
}
function git { $global:LASTEXITCODE=0; '1111111111111111111111111111111111111111' }
function Expect-Failure([hashtable]$Arguments,[string]$Expected){
    $caught=$false
    try { & (Join-Path $deploy 'Build-IisPackage.ps1') @Arguments }
    catch {
        if($_.Exception.Message -cne $Expected){throw}
        $caught=$true
    }
    if(-not $caught){throw "Expected failure: $Expected"}
}
try {
    $env:VSCMD_VER='fixture';$env:SETVARS_COMPLETED='1'
    $env:LAPLACE_SRC=$work
    $env:LAPLACE_WORK=Join-Path $work 'work'
    $env:LAPLACE_BUILD_ROOT=Join-Path $work 'managed'
    $storage=Join-Path $work 'Laplace-Operations/installation'
    $null=[IO.Directory]::CreateDirectory($storage)
    Set-Content -LiteralPath (Join-Path $storage 'storage.ps1') -Value 'function Enter-DesktopStorageOperation {}; function Exit-DesktopStorageOperation {}'
    $env:LAPLACE_ENGINE_BUILD=Join-Path $work 'native'
    $env:LAPLACE_DEVELOPMENT_INSTALLATION=Join-Path $work 'unconfigured/installation.json'
    Remove-Item Env:LAPLACE_IIS_INSTALLATION,Env:LAPLACE_API_KEY,Env:LAPLACE_OPERATOR_TOKEN -ErrorAction SilentlyContinue
    $buildBoundary='Component build failed; no application package was activated.'
    Expect-Failure @{} $buildBoundary
    $env:LAPLACE_IIS_INSTALLATION=Join-Path $work 'protected/installation.json'
    Expect-Failure @{} $buildBoundary
    Expect-Failure @{Installation=(Join-Path $work 'explicit/installation.json')} $buildBoundary
    Write-Host 'PASS ordinary package builds reach the component builder without installed IIS configuration or credentials.'

    Remove-Item Env:LAPLACE_IIS_INSTALLATION -ErrorAction SilentlyContinue
    Expect-Failure @{Publish=$true} 'Publication requires an IIS installation. Use -Installation or LAPLACE_IIS_INSTALLATION.'
    $env:LAPLACE_IIS_INSTALLATION=Join-Path $work 'protected/installation.json'
    Expect-Failure @{Publish=$true} 'Set LAPLACE_API_KEY for authenticated publication verification.'
    $env:LAPLACE_API_KEY='fixture-only-not-a-credential'
    Expect-Failure @{Publish=$true} 'fixture: installation read'
    Write-Host 'PASS publication checks its installation and workload credential before building.'

    # Controlled build/installer boundaries: assert configuration selection,
    # ordering and restoration without touching an installed database or DLL.
    $buildResult=0
    Set-Content -LiteralPath (Join-Path $deploy 'iis-runtime.ps1') -Value 'function Read-IisInstallation($Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }'
    $certificate=Join-Path $work 'test-root.crt'
    Set-Content -LiteralPath $certificate -Value 'fixture-only'
    $accessPath=Join-Path $work 'access.json'
    @{database_host=$env:COMPUTERNAME;port=5432;authentication='sspi';admin_role='fixture_owner';root_certificate=$certificate} |
        ConvertTo-Json | Set-Content -LiteralPath $accessPath
    $env:LAPLACE_IIS_INSTALLATION=Join-Path $work 'installation.json'
    @{database_access_file=$accessPath;database=@{host=$env:COMPUTERNAME;port=5432}} |
        ConvertTo-Json | Set-Content -LiteralPath $env:LAPLACE_IIS_INSTALLATION
    $env:LAPLACE_TEST_CHECK_FILE=Join-Path $work 'preflight-ran'
    Set-Content -LiteralPath (Join-Path $scripts 'install-extensions.cmd') -Encoding Ascii -Value @'
@echo off
if not "%~1"=="--check-connection" exit /b 91
if defined PGSERVICE exit /b 92
if defined PGHOSTADDR exit /b 93
if defined PGOPTIONS exit /b 94
if defined PGPASSWORD exit /b 95
if not "%PGSSLMODE%"=="verify-full" exit /b 96
if not "%PGREQUIREAUTH%"=="sspi" exit /b 97
if not "%LAPLACE_PGHOST%"=="%COMPUTERNAME%" exit /b 98
if not "%LAPLACE_PGUSER%"=="fixture_owner" exit /b 99
if not "%PGBIN%"=="fixture-postgresql-tools" exit /b 100
echo checked>"%LAPLACE_TEST_CHECK_FILE%"
exit /b 17
'@
    Set-Content -LiteralPath (Join-Path $scripts 'build-extensions.cmd') -Encoding Ascii -Value '@echo unexpected>"%LAPLACE_TEST_CHECK_FILE%.build"'
    foreach($name in 'PGSERVICE','PGHOSTADDR','PGOPTIONS','PGPASSWORD','LAPLACE_PGHOST','LAPLACE_PGPORT','LAPLACE_PGUSER'){
        [Environment]::SetEnvironmentVariable($name,'ambient-fixture-value')
    }
    $env:PGBIN='fixture-postgresql-tools'
    Expect-Failure @{Publish=$true} 'PostgreSQL extension connection preflight failed; installed extension files were not changed.'
    if(-not(Test-Path -LiteralPath $env:LAPLACE_TEST_CHECK_FILE)){throw 'Installer did not receive the isolated declared SSPI configuration.'}
    if(Test-Path -LiteralPath ($env:LAPLACE_TEST_CHECK_FILE+'.build')){throw 'Extension build ran after failed connection preflight.'}
    foreach($name in 'PGSERVICE','PGHOSTADDR','PGOPTIONS','PGPASSWORD','LAPLACE_PGHOST','LAPLACE_PGPORT','LAPLACE_PGUSER'){
        if([Environment]::GetEnvironmentVariable($name) -cne 'ambient-fixture-value'){throw "Caller environment was not restored: $name"}
    }
    Write-Host 'PASS declared SSPI connection is isolated from ambient settings; failed preflight stops extension installation and restores caller state.'
} finally {
    foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
    $resolved=[IO.Path]::GetFullPath($work)
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected test directory outside temporary storage.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
exit 0
