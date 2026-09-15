param([Parameter(Mandatory = $true)][string]$EnvironmentFile)
$ErrorActionPreference = 'Stop'

# Stockfish's upstream Makefile requires a GNU-compatible Windows toolchain.
# Reuse an existing complete installation before acquiring MSYS2/UCRT64.
function Find-CompatibleCompiler {
    foreach ($candidate in @(@('g++', 'gcc'), @('clang++', 'clang'))) {
        $command = Get-Command $candidate[0] -ErrorAction SilentlyContinue
        if (-not $command) { continue }
        $target = & $command.Source -dumpmachine 2>$null
        if ($LASTEXITCODE -eq 0 -and "$target" -match '(mingw|windows-gnu)') {
            return $candidate[1]
        }
    }
    return $null
}

function Have-BuildTools {
    foreach ($name in @('git', 'make', 'sh', 'curl', 'sha256sum', 'grep', 'sed', 'cut', 'head', 'tail')) {
        if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { return $false }
    }
    return [bool](Find-CompatibleCompiler)
}

$toolPath = ''
if (-not (Have-BuildTools)) {
    $roots = @($env:MSYS2_ROOT, (Join-Path $env:LAPLACE_TOOLS 'msys64'), 'C:\msys64') | Where-Object { $_ }
    $msys = $roots | Where-Object { Test-Path (Join-Path $_ 'usr\bin\bash.exe') } | Select-Object -First 1
    if (-not $msys) {
        $msys = if ($env:MSYS2_ROOT) { $env:MSYS2_ROOT } else { Join-Path $env:LAPLACE_TOOLS 'msys64' }
        if (Test-Path $msys) { throw "Existing incomplete MSYS2 directory is preserved: $msys" }
        $downloads = Join-Path $env:LAPLACE_TOOLS 'downloads'
        New-Item -ItemType Directory -Force $downloads | Out-Null
        $release = Invoke-RestMethod 'https://api.github.com/repos/msys2/msys2-installer/releases/latest'
        $asset = $release.assets | Where-Object { $_.name -match '^msys2-x86_64-[0-9]+\.exe$' } | Select-Object -First 1
        if (-not $asset) { throw 'The official MSYS2 stable release has no x86-64 installer' }
        $installer = Join-Path $downloads $asset.name
        Invoke-WebRequest $asset.browser_download_url -OutFile $installer
        $expected = if ($asset.digest -match '^sha256:([0-9a-f]{64})$') {
            $Matches[1]
        } else {
            $checksum = Invoke-RestMethod ($asset.browser_download_url + '.sha256')
            if ("$checksum" -notmatch '^([0-9a-fA-F]{64})\s') { throw 'MSYS2 checksum is missing' }
            $Matches[1]
        }
        if ((Get-FileHash $installer -Algorithm SHA256).Hash -ne $expected) { throw 'MSYS2 installer SHA-256 mismatch' }
        # Official unattended installer interface; the user's tools drive owns it.
        & $installer in --confirm-command --accept-messages --root ($msys -replace '\\', '/')
        if ($LASTEXITCODE -ne 0) { throw "MSYS2 installation failed: $LASTEXITCODE" }
    }
    $bash = Join-Path $msys 'usr\bin\bash.exe'
    $env:MSYSTEM = 'UCRT64'
    $env:CHERE_INVOKING = 'yes'
    # MSYS2 documents two update passes when core runtime packages change.
    & $bash -lc 'pacman --noconfirm -Syuu'
    if ($LASTEXITCODE -ne 0) { throw "MSYS2 core update failed: $LASTEXITCODE" }
    & $bash -lc 'pacman --noconfirm -Syuu'
    if ($LASTEXITCODE -ne 0) { throw "MSYS2 package update failed: $LASTEXITCODE" }
    & $bash -lc 'pacman --noconfirm -S --needed make git curl coreutils grep sed mingw-w64-ucrt-x86_64-gcc'
    if ($LASTEXITCODE -ne 0) { throw "Stockfish build prerequisites failed: $LASTEXITCODE" }
    $toolPath = (Join-Path $msys 'ucrt64\bin') + ';' + (Join-Path $msys 'usr\bin')
    $env:PATH = "$toolPath;$env:PATH"
    if (-not (Have-BuildTools)) { throw 'Installed MSYS2 does not provide the required Stockfish source toolchain' }
}
$compiler = Find-CompatibleCompiler
New-Item -ItemType Directory -Force (Split-Path -Parent $EnvironmentFile) | Out-Null
@("LAPLACE_STOCKFISH_TOOLCHAIN_PATH=$toolPath", "LAPLACE_STOCKFISH_COMP=$compiler") |
    Set-Content -LiteralPath $EnvironmentFile -Encoding utf8NoBOM
Write-Host "Stockfish source toolchain ready: $compiler"
