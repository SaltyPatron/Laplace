#requires -Version 7
# Update the shared source checkout, build the CLI, and deploy Qt DLLs/plugins.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
function Invoke-Checked {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE" }
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$lock = Get-Content -Raw (Join-Path $repo 'deploy\cutechess-release.json') | ConvertFrom-Json
$helper = Join-Path $repo 'scripts\provision-cutechess.py'
$build = $env:LAPLACE_CUTECHESS_BUILD
if (-not $build -or -not $env:LAPLACE_TOOLS) { throw 'Run scripts\win\build-cutechess.cmd to load the build environment' }
$external = if ($env:LAPLACE_EXTERNAL) { $env:LAPLACE_EXTERNAL } else { Join-Path $repo 'external' }
$work = Join-Path $env:LAPLACE_BUILD_ROOT 'work\chess-tools'
New-Item -ItemType Directory -Force -Path $work, $build | Out-Null
$env:TMPDIR = $env:TMP = $env:TEMP = $work
$source = & python $helper --source-dir (Join-Path $external 'cutechess')
if ($LASTEXITCODE -ne 0) { throw 'CuteChess source update failed' }
$source = "$source".Trim()
$qtRoot = if ($env:LAPLACE_QT_ROOT) { $env:LAPLACE_QT_ROOT } else { Join-Path $env:LAPLACE_DEPS_PREFIX 'qt' }
$qt = & python (Join-Path $repo 'scripts\provision-chess-qt.py') --root $qtRoot --work $work
if ($LASTEXITCODE -ne 0) { throw 'Qt SDK provisioning failed' }
$qt = "$qt".Trim()
$vcvars = $env:LAPLACE_VCVARS
if (-not $vcvars) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($vs) { $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat' }
    }
}
if (-not $vcvars -or -not (Test-Path $vcvars)) { throw 'MSVC x64 C++ build tools missing; install Visual Studio C++ Build Tools or set LAPLACE_VCVARS' }
$vcEnvironment = & $env:ComSpec /d /s /c "`"`"$vcvars`" >nul && set`""
if ($LASTEXITCODE -ne 0) { throw 'MSVC environment setup failed' }
foreach ($line in $vcEnvironment) {
    if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
}
$zstdSource = if ($env:LAPLACE_ZSTD_SOURCE) { $env:LAPLACE_ZSTD_SOURCE } else { Join-Path $external 'zstd' }
$zstdBuild = if ($env:LAPLACE_ZSTD_BUILD) { $env:LAPLACE_ZSTD_BUILD } else { Join-Path $env:LAPLACE_BUILD_ROOT 'build-zstd' }
Invoke-Checked python @((Join-Path $repo 'scripts\install-zstd.py'), '--source-dir', $zstdSource, '--build-dir', $zstdBuild)
Invoke-Checked cmake @('--fresh', '-S', $source, '-B', $build, '-G', 'Ninja',
    '-DCMAKE_BUILD_TYPE=Release', '-DWITH_TESTS=OFF', '-DCMAKE_C_COMPILER=cl',
    '-DCMAKE_CXX_COMPILER=cl', "-DCMAKE_PREFIX_PATH=$qt")
Invoke-Checked cmake @('--build', $build, '--target', 'cli')
$binary = Join-Path $build 'cutechess-cli.exe'
Invoke-Checked (Join-Path $qt 'bin\windeployqt.exe') @('--release', '--compiler-runtime', '--no-translations', '--dir', $build, $binary)
Invoke-Checked python @($helper, '--binary', $binary, '--qt-version', $lock.qt_version)
Write-Host "CuteChess $($lock.version) and Qt $($lock.qt_version) ready: $binary"
