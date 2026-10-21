#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PostgresBin,
    [Parameter(Mandatory)][int]$Port,
    [Parameter(Mandatory)][string]$Role
)
# Runs only read-only connection checks against a caller-owned test cluster.
$ErrorActionPreference='Stop'
$installer=[IO.Path]::GetFullPath("$PSScriptRoot/../scripts/win/install-extensions.cmd")
$work=Join-Path ([IO.Path]::GetTempPath()) ('extension-preflight-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $work
$names=@('LAPLACE_ENV_LOADED','LAPLACE_ROOT','LAPLACE_DEPLOY','LAPLACE_PGHOST','LAPLACE_PGPORT','LAPLACE_PGUSER','PGBIN','PSQLRC',
    'PGSSLMODE','PGSSLROOTCERT','PGREQUIREAUTH','PGCONNECT_TIMEOUT','PGSERVICE','PGSERVICEFILE','PGHOSTADDR','PGOPTIONS','PGPASSWORD','PGPASSFILE')
$saved=@{}
foreach($name in $names){$saved[$name]=[Environment]::GetEnvironmentVariable($name)}
function Invoke-Preflight([string[]]$Arguments){
    $output=@(& $installer @Arguments 2>&1)
    @{exit_code=$LASTEXITCODE;output=($output -join "`n")}
}
try {
    foreach($name in $names){Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue}
    $env:LAPLACE_ENV_LOADED='1';$env:LAPLACE_ROOT=[IO.Path]::GetFullPath("$PSScriptRoot/..")
    $env:LAPLACE_DEPLOY=Join-Path $work 'must-not-be-created'
    $env:PGBIN=$PostgresBin;$env:LAPLACE_PGHOST='127.0.0.1';$env:LAPLACE_PGPORT=[string]$Port
    $env:LAPLACE_PGUSER=$Role;$env:PGSSLMODE='disable';$env:PGCONNECT_TIMEOUT='2'
    $env:PSQLRC=Join-Path $work 'psqlrc'
    Set-Content -LiteralPath $env:PSQLRC -Value '\quit 99'
    $result=Invoke-Preflight @('--check-connection')
    if($result.exit_code -ne 0 -or $result.output -notmatch [regex]::Escape($Role+'|')){throw "Connection-only check failed: $($result.output)"}
    if(Test-Path -LiteralPath $env:LAPLACE_DEPLOY){throw 'Connection-only preflight modified deployment files.'}
    Write-Host 'PASS actual PostgreSQL identity returned; connection-only check ignores psqlrc and writes no deployment files.'

    $env:LAPLACE_PGUSER='missing_preflight_'+[Guid]::NewGuid().ToString('N')
    $result=Invoke-Preflight @('--skip-build')
    if($result.exit_code -eq 0 -or $result.output -notmatch 'FATAL:' -or
        $result.output -notmatch 'No extension files or server settings were changed' -or
        $result.output -match 'Start postgresql-x64-18'){
        throw "The real authentication error was lost: $($result.output)"
    }
    if(Test-Path -LiteralPath $env:LAPLACE_DEPLOY){throw 'Failed authentication modified deployment files.'}
    Write-Host 'PASS real server authentication error retained; normal installation stops before copying files.'

    $result=Invoke-Preflight @('--check-connection','--files-only')
    if($result.exit_code -ne 2){throw 'Incompatible flags were accepted.'}
    Write-Host 'PASS conflicting connection-only and offline-file modes rejected.'

    $env:LAPLACE_PGUSER=$Role
    # Exercise the installer's generated SQL with PostgreSQL itself. No serving
    # cluster or backend is selected: this test uses only the supplied loopback
    # test cluster, and the rejection cases inject false operation results.
    $lines=Get-Content -LiteralPath $installer
    foreach($operation in 'reload','recycle'){
        $line=if($operation -eq 'reload'){
            $lines | Where-Object {$_ -like '  echo DO $$ BEGIN IF NOT pg_reload_conf*'}
        }else{$lines | Where-Object {$_ -like '  > "%GUC_SQL%" echo DO $$ DECLARE recycled_count*'}}
        if(@($line).Count -ne 1){throw "Missing maintained $operation SQL owner."}
        $echo=$line.Substring($line.IndexOf('echo DO'))
        $generate=Join-Path $work 'generate.cmd'
        Set-Content -LiteralPath $generate -Encoding Ascii -Value ("@echo off`r`n"+$echo)
        $sql=(& $generate) -join "`n"
        if($LASTEXITCODE){throw 'SQL generation failed.'}
        # Do not terminate another test's connections. Select an empty set for
        # success, and this connection with a false result for rejection.
        if($operation -eq 'recycle'){
            $sql=$sql.Replace("datname LIKE 'laplace%' AND pid <> pg_backend_pid()",'false')
        }
        $result=@($sql | & "$PostgresBin/psql.exe" -X -w -h 127.0.0.1 -p $Port -U $Role -d postgres -v ON_ERROR_STOP=1 -f - 2>&1)
        if($LASTEXITCODE){throw ($result -join "`n")}
        $rejected=if($operation -eq 'reload'){$sql.Replace('pg_reload_conf()','false')}else{
            $sql.Replace('pg_terminate_backend(pid)','false').Replace('WHERE false','WHERE pid = pg_backend_pid()')
        }
        $result=@($rejected | & "$PostgresBin/psql.exe" -X -w -h 127.0.0.1 -p $Port -U $Role -d postgres -v ON_ERROR_STOP=1 -f - 2>&1)
        if($LASTEXITCODE -ne 3 -or ($result -join "`n") -notmatch 'ERROR:'){
            throw "False $operation result did not fail psql: $($result -join "`n")"
        }
        Write-Host "PASS generated $operation SQL: success returns zero; false result raises an error and exit 3."
    }
} finally {
    foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
    $resolved=[IO.Path]::GetFullPath($work)
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected test directory outside temporary storage.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
