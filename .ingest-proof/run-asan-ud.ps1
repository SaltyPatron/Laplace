$ErrorActionPreference='Continue'
$exeDir='D:\Repositories\Laplace\app\Laplace.Cli\bin\Release\net10.0'
$asanBase='D:\Repositories\Laplace\build-win-asan'
$map = @{ 'laplace_core.dll'="$asanBase\core\laplace_core.dll"; 'laplace_dynamics.dll'="$asanBase\dynamics\laplace_dynamics.dll"; 'laplace_synthesis.dll'="$asanBase\synthesis\laplace_synthesis.dll" }
foreach ($k in $map.Keys) { Copy-Item "$exeDir\$k" "$exeDir\$k.nonasan" -Force; Copy-Item $map[$k] "$exeDir\$k" -Force }
$intel='C:\Program Files (x86)\Intel\oneAPI'
$asanRt="$intel\compiler\latest\lib\clang\21\lib\windows"
$env:PATH = @($asanRt, "$intel\tbb\latest\bin", "$intel\mkl\latest\bin", "$intel\compiler\latest\bin",
              'C:\Program Files\PostgreSQL\18\bin', $exeDir, "$asanBase\core", "$asanBase\dynamics", "$asanBase\synthesis") -join ';' + ';' + $env:PATH
$env:LAPLACE_ROOT='D:\Repositories\Laplace'
$env:LAPLACE_PERFCACHE_BIN='D:\Repositories\Laplace\build-win\core\perfcache\laplace_t0_perfcache.bin'
$env:PGPASSWORD='postgres'
$env:LAPLACE_DB='Host=localhost;Username=postgres;Password=postgres;Database=laplace'
$env:LAPLACE_SKIP_MKL_CHECK='1'
$env:LAPLACE_DECOMPOSE_WORKERS='4'
$env:ASAN_OPTIONS='halt_on_error=1:abort_on_error=0:detect_leaks=0:print_stats=0'
try { & "$exeDir\Laplace.Cli.exe" ingest ud *>&1 | Tee-Object 'D:\Repositories\Laplace\.ingest-proof\asan-ud.out' }
finally { foreach ($k in $map.Keys) { Copy-Item "$exeDir\$k.nonasan" "$exeDir\$k" -Force; Remove-Item "$exeDir\$k.nonasan" -Force -ErrorAction SilentlyContinue }; Write-Output "=== DLLs restored ===" }
