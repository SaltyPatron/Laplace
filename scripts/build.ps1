<#
.SYNOPSIS
  Build the Laplace native extension + standalone shared library + .NET solution.

.DESCRIPTION
  Two phases run in this order:
    1. CMake configure + build of ext/laplace_pg. Produces:
         build/<config>/laplace_pg.{dll,so,dylib}      — PostgreSQL extension
         build/<config>/laplace_native.{dll,so,dylib}  — managed P/Invoke runtime
    2. dotnet build of Laplace.slnx. The native runtime is staged into each
       .NET project's bin/ via Directory.Build.props + native-dll.targets.

  Strict mode: any warning (native or managed) fails the build.

.PARAMETER Configuration
  Release (default) or Debug.

.PARAMETER Asan
  Pass to enable AddressSanitizer for the native build (-DLAPLACE_ASAN=ON).

.PARAMETER NativeOnly
  Skip the .NET build.

.PARAMETER ManagedOnly
  Skip the CMake build (assumes laplace_native is already present).
#>

[CmdletBinding()]
param(
  [ValidateSet('Release','Debug')] [string]$Configuration = 'Release',
  [switch]$Asan,
  [switch]$NativeOnly,
  [switch]$ManagedOnly
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$repoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..')
$nativeDir  = Join-Path $repoRoot 'ext\laplace_pg'
$nativeBuild = Join-Path $nativeDir 'build'

Write-Host "Laplace build" -ForegroundColor Cyan
Write-Host "  Repo:          $repoRoot"
Write-Host "  Configuration: $Configuration"
Write-Host "  ASan:          $Asan"
Write-Host ""

if (-not $ManagedOnly) {
  Write-Host "[1/2] Native build (CMake)" -ForegroundColor Yellow
  $cmakeArgs = @(
    '-S', $repoRoot,
    '-B', $nativeBuild,
    "-DCMAKE_BUILD_TYPE=$Configuration"
  )
  if ($Asan) { $cmakeArgs += '-DLAPLACE_ASAN=ON' }

  & cmake @cmakeArgs
  if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }

  & cmake --build $nativeBuild --config $Configuration --parallel
  if ($LASTEXITCODE -ne 0) { throw "CMake build failed" }
  Write-Host "  Native build OK" -ForegroundColor Green
}

if (-not $NativeOnly) {
  Write-Host ""
  Write-Host "[2/2] Managed build (dotnet)" -ForegroundColor Yellow
  Push-Location $repoRoot
  try {
    & dotnet build Laplace.slnx --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
  } finally {
    Pop-Location
  }
  Write-Host "  Managed build OK" -ForegroundColor Green
}

Write-Host ""
Write-Host "Build complete." -ForegroundColor Cyan
