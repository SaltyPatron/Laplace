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
Set-Content -LiteralPath (Join-Path $scripts 'build-engine.cmd') -Encoding Ascii -Value "@exit /b 73`r`n"
$names=@('VSCMD_VER','SETVARS_COMPLETED','LAPLACE_IIS_INSTALLATION','LAPLACE_API_KEY','LAPLACE_ENGINE_BUILD','LAPLACE_ROOT')
$saved=@{}
foreach($name in $names){$saved[$name]=[Environment]::GetEnvironmentVariable($name)}
function icx.exe {}
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
    $env:LAPLACE_ENGINE_BUILD=Join-Path $work 'native'
    Remove-Item Env:LAPLACE_IIS_INSTALLATION,Env:LAPLACE_API_KEY -ErrorAction SilentlyContinue
    $buildBoundary='Native runtime build failed; no application package was created.'
    Expect-Failure @{} $buildBoundary
    $env:LAPLACE_IIS_INSTALLATION=Join-Path $work 'protected/installation.json'
    Expect-Failure @{} $buildBoundary
    Expect-Failure @{Installation=(Join-Path $work 'explicit/installation.json')} $buildBoundary
    Write-Host 'PASS ordinary package builds reach the native builder without installed IIS configuration or credentials.'

    Remove-Item Env:LAPLACE_IIS_INSTALLATION -ErrorAction SilentlyContinue
    Expect-Failure @{Publish=$true} 'Publication requires an IIS installation. Use -Installation or LAPLACE_IIS_INSTALLATION.'
    $env:LAPLACE_IIS_INSTALLATION=Join-Path $work 'protected/installation.json'
    Expect-Failure @{Publish=$true} 'Set LAPLACE_API_KEY for authenticated publication verification.'
    $env:LAPLACE_API_KEY='fixture-only-not-a-credential'
    Expect-Failure @{Publish=$true} 'fixture: installation read'
    Write-Host 'PASS publication checks its installation and workload credential before building.'
} finally {
    foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
    $resolved=[IO.Path]::GetFullPath($work)
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected test directory outside temporary storage.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
