#Requires -Version 7
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not(Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs=(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
    if(-not $vs){throw 'MSVC is required to exercise the actual modular native ABI closure.'}
    $vcvars=Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
    $lines=& cmd /d /c "`"$vcvars`" >nul && set"
    if($LASTEXITCODE){throw 'MSVC initialization failed.'}
    foreach($line in $lines){if($line -match '^([^=]+)=(.*)$'){[Environment]::SetEnvironmentVariable($Matches[1],$Matches[2])}}
}
$scratch=if($env:RUNNER_TEMP){$env:RUNNER_TEMP}elseif($env:LAPLACE_WORK){Join-Path $env:LAPLACE_WORK 'package-checks'}else{throw 'Declare RUNNER_TEMP or LAPLACE_WORK.'}
$work=Join-Path $scratch ('native-package-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $work -Force
try {
    foreach($kind in 'current','incomplete','stale') {
        $directory=Join-Path $work $kind
        $null=New-Item -ItemType Directory -Path $directory
        @'
__declspec(dllexport) int lp_id_codepoint(unsigned int cp, void *out) { (void)cp; (void)out; return 0; }
__declspec(dllexport) int lp_id_compose(const void *ids, unsigned long long n, void *out) { (void)ids; (void)n; (void)out; return 0; }
__declspec(dllexport) int lp_coord_centroid(const void *coords, unsigned long long n, void *out) { (void)coords; (void)n; (void)out; return 0; }
__declspec(dllexport) int lp_tier0_codepoint(unsigned int cp, void *out) { (void)cp; (void)out; return 0; }
'@ | Set-Content -LiteralPath (Join-Path $directory 'laplace_native_stub.c') -Encoding utf8
        $turnExport = if ($kind -eq 'incomplete') { '' } else { '__declspec(dllexport) int lp_engine_turn(void *h, const void *req, void *out) { (void)h; (void)req; (void)out; return 0; }' }
        $versionMarker = if ($kind -eq 'stale') { 'stale-cli-abi' } else { 'current-abi' }
        @"
__declspec(dllexport) const char *lp_fixture_version(void) { return "$versionMarker"; }
__declspec(dllexport) void *lp_engine_init(const char *c, char *e, unsigned long long cap) { (void)c; (void)e; (void)cap; return 0; }
$turnExport
__declspec(dllexport) void lp_engine_turn_response_free(void *out) { (void)out; }
__declspec(dllexport) int lp_engine_chess_play(void *h, const void *req, void *out) { (void)h; (void)req; (void)out; return 0; }
__declspec(dllexport) int lp_engine_mold_checkpoint(void *h, const void *req, void *out) { (void)h; (void)req; (void)out; return 0; }
__declspec(dllexport) void lp_engine_free(void *h) { (void)h; }
__declspec(dllexport) int lp_text_id(const char *s, unsigned long long n, void *out) { (void)s; (void)n; (void)out; return 0; }
__declspec(dllexport) int lp_id_compose(const void *ids, unsigned long long n, void *out) { (void)ids; (void)n; (void)out; return 0; }
"@ | Set-Content -LiteralPath (Join-Path $directory 'laplace_engine_abi_stub.c') -Encoding utf8
        Push-Location $directory
        try {
            & cl.exe /nologo /LD /utf-8 laplace_native_stub.c /link /OUT:laplace.dll | Out-Host
            if($LASTEXITCODE){throw 'Native identity stub compilation failed.'}
            & cl.exe /nologo /LD /utf-8 laplace_engine_abi_stub.c /link /OUT:laplace_engine_abi.dll | Out-Host
            if($LASTEXITCODE){throw 'Engine ABI stub compilation failed.'}
        } finally {Pop-Location}
    }

    & "$repo/deploy/windows/Test-NativeSqlCatalog.ps1" -Library "$work/current/laplace_engine_abi.dll"

    $missingRejected=$false
    try { & "$repo/deploy/windows/Test-NativeSqlCatalog.ps1" -Library "$work/incomplete/laplace_engine_abi.dll" }
    catch {
        if($_.Exception.Message -notmatch 'Native engine ABI export is missing: lp_engine_turn'){throw}
        $missingRejected=$true
    }
    if(-not $missingRejected){throw 'Incomplete native ABI library was not rejected.'}

    $published=Join-Path $work 'published'
    $null=New-Item -ItemType Directory -Path "$published/cli","$published/uci"
    foreach($component in @('','uci')) {
        Copy-Item "$work/current/laplace.dll","$work/current/laplace_engine_abi.dll" (Join-Path $published $component)
        [IO.File]::WriteAllText((Join-Path (Join-Path $published $component) 'laplace.exe'),'engine-cli')
    }
    Copy-Item "$work/stale/laplace.dll","$work/stale/laplace_engine_abi.dll" "$published/cli"
    [IO.File]::WriteAllText("$published/cli/laplace.exe",'engine-cli')
    [IO.File]::WriteAllText("$published/uci/laplace-uci.exe",'unused: native closure rejection must precede package creation')
    $destination=Join-Path $work 'package'
    $rejected=$false
    try { & "$repo/deploy/windows/New-IisPackage.ps1" -Published $published -Destination $destination -Revision ('1'*40) }
    catch {
        if($_.Exception.Message -notmatch 'Native runtime binary differs between application components:'){throw}
        $rejected=$true
    }
    if(-not $rejected -or (Test-Path -LiteralPath $destination)){throw 'A stale CLI native binary reached package creation.'}
    Write-Host 'PASS: current modular native ABI accepted; incomplete exports and stale CLI binary rejected before package creation.'
} finally {
    if(Test-Path -LiteralPath $work){Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue}
}
