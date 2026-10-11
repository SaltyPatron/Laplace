#Requires -Version 7
[CmdletBinding()]
param([string]$Destination)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/laplace-paths.ps1"
Import-LaplaceEnv

if (-not $Destination) {
    if (-not [IO.Path]::IsPathFullyQualified($env:LAPLACE_CLI_EXE) -or
        [IO.Path]::GetFileName($env:LAPLACE_CLI_EXE) -ne 'Laplace.Cli.exe') {
        throw 'LAPLACE_CLI_EXE must select an absolute Laplace.Cli.exe publish path.'
    }
    $Destination = Split-Path $env:LAPLACE_CLI_EXE
}
$Destination = [IO.Path]::GetFullPath($Destination)
$executable = Join-Path $Destination 'Laplace.Cli.exe'
$repo = Get-LaplaceRepoRoot

& py -3 (Join-Path $repo 'scripts/install-zstd.py')
if ($LASTEXITCODE) { throw 'The locked Zstandard runtime could not be prepared.' }
& dotnet publish (Join-Path $repo 'app/Laplace.Cli/Laplace.Cli.csproj') -c Release `
    -p:Platform=AnyCPU -p:PublishReadyToRun=true --no-self-contained -o $Destination --nologo
if ($LASTEXITCODE) { throw 'CLI publication failed.' }
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "CLI publication did not produce $executable"
}

# The published executable must carry its native closure and Tier-0 map. The
# compiler shell, checkout and build variables must not make a broken package pass.
foreach ($check in @(
    @{ Arguments = @('--help'); Expected = 'ingest' },
    @{ Arguments = @('decompose', 'Laplace'); Expected = 'nodes' }
)) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = [Environment]::GetFolderPath('UserProfile')
    foreach ($argument in $check.Arguments) { $start.ArgumentList.Add($argument) }
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($key in @($start.Environment.Keys)) {
        if ($key -like 'LAPLACE_*' -or $key -in @('MKLROOT','TBBROOT','CMPLR_ROOT','ONEAPI_ROOT','LIB','INCLUDE')) {
            [void]$start.Environment.Remove($key)
        }
    }
    $start.Environment['PATH'] = [Environment]::GetFolderPath('System')
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Published CLI startup timed out.' }
        $text = $output.GetAwaiter().GetResult()
        $errorText = $errorOutput.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or $text -notmatch $check.Expected) {
            throw "Published CLI $($check.Arguments[0]) failed outside the developer environment (exit $($process.ExitCode)): $errorText $text"
        }
        Write-Host "CLI $($check.Arguments[0]) passed outside the developer environment."
    } finally { $process.Dispose() }
}
Set-Content -LiteralPath ($executable + '.r2r-stamp') -Value ('r2r ' + [DateTime]::UtcNow.ToString('o'))
Write-Host "Monorepo CLI: $executable"
