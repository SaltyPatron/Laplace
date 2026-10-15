#Requires -Version 7
[CmdletBinding()]
param([string]$NpgsqlAssembly)
$ErrorActionPreference='Stop'
if($NpgsqlAssembly){$null=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($NpgsqlAssembly))}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$work=Join-Path ([IO.Path]::GetTempPath()) ('laplace-maintenance-test-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path "$work/site/web/releases/abc1234/migrations","$work/work"
$saved=@{}
foreach($item in Get-ChildItem Env:){$saved[$item.Name]=$item.Value}
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
try {
    $env:LAPLACE_MACHINE_ENV="$work/machine.env"
    '# isolated configuration fixture' | Set-Content $env:LAPLACE_MACHINE_ENV
    $env:LAPLACE_WORK="$work/work";$env:LAPLACE_PGDATA="$work/pgdata"
    $env:TEST_EVENTS="$work/events.txt";$env:TEST_DATA=$env:LAPLACE_PGDATA
    'runtime fixture' | Set-Content "$work/site/web/releases/abc1234/migrations/Laplace.Migrations.dll"
    'certificate fixture' | Set-Content "$work/root.crt"
    '<configuration><system.webServer><aspNetCore processPath=".\releases\abc1234\Laplace.Endpoints.OpenAICompat.exe"/></system.webServer></configuration>' | Set-Content "$work/site/web/web.config"
    $declaration=@{
        version=1;host=$env:COMPUTERNAME.ToLowerInvariant();site='fixture';pool='fixture';role='fixture';revision='abc1234'
        directory="$work/site";publish="$work/publish";bao="$work/bao";machine_env=$env:LAPLACE_MACHINE_ENV
        origin='https://fixture.invalid';address='https://fixture.invalid'
        records=@{api='kv/data/api';identity='kv/data/identity';certificate='kv/data/certificate';chess='kv/data/chess'}
        database=@{host=$env:COMPUTERNAME;port=5432;pool_max=4;authentication='sspi';substrate_database='content';application_database='accounts';substrate_role='content_runtime';application_role='accounts_runtime'}
    }
    $access=@{authentication='sspi';database_host=$env:COMPUTERNAME;port=5432;admin_role='operator';application_owner='accounts_owner';psql="$work/psql.ps1";root_certificate="$work/root.crt";substrate_database='content';application_database='accounts';substrate_role='content_runtime';application_role='accounts_runtime'}
    $declaration | ConvertTo-Json -Depth 6 | Set-Content "$work/installation.json"
    $access | ConvertTo-Json | Set-Content "$work/access.json"
    @'
$sql=($input | Out-String).Trim()
if($env:PGREQUIREAUTH -cne 'sspi'){throw 'psql requires the libpq authentication spelling.'}
Add-Content -LiteralPath $env:TEST_EVENTS -Value ('sql:'+$sql)
$global:LASTEXITCODE=0
if($sql -eq 'SELECT system_identifier FROM pg_control_system();'){'fixture-cluster'}
elseif($sql -eq 'SHOW data_directory;'){$env:TEST_DATA}
elseif($sql -like 'SELECT count(*) FROM pg_roles*'){'1'}
elseif($sql -like 'SELECT count(*) FROM pg_available_extensions*'){if($env:TEST_EXTENSION_MISSING -eq '1'){'0'}else{'1'}}
elseif($sql -like 'SELECT nspname*'){'public';'laplace'}
'@ | Set-Content "$work/psql.ps1"
    function global:dotnet {
        $verb=$args[1]
        if($verb -eq '--help'){
            $global:LASTEXITCODE=0
            if($env:TEST_OLD_RUNTIME -eq '1'){'up';'status';'reset';'nuke';return}
            'app-status';'app-up';'app-check';'app-reset';'app-nuke';'substrate-up';'substrate-status';'reset';'nuke'
            return
        }
        foreach($connection in $env:LAPLACE_DB,$env:LAPLACE_APP_DB){
            $builder=[System.Data.Common.DbConnectionStringBuilder]::new()
            $builder.set_ConnectionString($connection)
            $auth=if($builder.ContainsKey('Require Auth')){[string]$builder['Require Auth']}else{$env:PGREQUIREAUTH}
            if('Npgsql.NpgsqlConnectionStringBuilder' -as [type]){
                $driver=[Npgsql.NpgsqlConnectionStringBuilder]::new($connection)
                $parser=$driver.GetType().GetMethod('ParseAuthMode',[Reflection.BindingFlags]'NonPublic,Static')
                if($parser.Invoke($null,@($auth)).ToString() -cne 'SSPI'){throw 'Migration authentication must require SSPI.'}
            }elseif($auth -cne 'SSPI'){throw 'The migration connection must use the Npgsql authentication spelling.'}
        }
        Add-Content -LiteralPath $env:TEST_EVENTS -Value ('migration:'+$verb)
        $global:LASTEXITCODE=if($verb -eq $env:TEST_FAIL_VERB){23}else{0}
    }
    $invoke=@{Installation="$work/installation.json";AccessConfiguration="$work/access.json";Yes=$true}
    $command=Join-Path $root 'deploy/windows/Invoke-IisDatabase.ps1'
    foreach($case in @(@('application','repair','app-up','app-check'),@('application','recreate','app-nuke','app-check'),@('substrate','repair','substrate-up','substrate-status'))){
        '' | Set-Content $env:TEST_EVENTS
        $env:LAPLACE_DB='ambient-connection';$env:PGPASSWORD='ambient-secret-fixture'
        & $command @invoke -Database $case[0] -Operation $case[1]
        $events=Get-Content $env:TEST_EVENTS
        $migrations=@($events | Where-Object {$_ -like 'migration:*'})
        Assert (($migrations -join ',') -ceq ('migration:'+$case[2]+',migration:'+$case[3])) 'Wrong database migration commands executed.'
        $grants=@($events | Where-Object {$_ -like 'sql:GRANT*' -or $_ -like 'sql:REVOKE*'})
        Assert ($grants.Count -gt 0) 'Runtime grants were not applied.'
        if($case[0] -eq 'substrate'){
            Assert (@($events | Where-Object {$_ -like 'sql:GRANT EXECUTE*'}).Count -gt 0) 'Substrate function permissions were not restored.'
            Assert (@($events | Where-Object {$_ -like 'sql:SET ROLE*laplace.word_id*'}).Count -eq 1) 'Substrate tables and cache were not checked as the runtime role.'
        }
        Assert ($env:LAPLACE_DB -ceq 'ambient-connection' -and $env:PGPASSWORD -ceq 'ambient-secret-fixture') 'Process environment was not restored.'
        Write-Host "PASS $($case[0]) $($case[1]): scoped commands, runtime grants, environment restoration"
    }
    '' | Set-Content $env:TEST_EVENTS
    $configLock=$null;$publicationLock=$null
    $originalWork=$env:LAPLACE_WORK
    try {
        # Exclusive handles reproduce inaccessible IIS files without changing ACLs.
        $configLock=[IO.File]::Open("$work/site/web/web.config",'Open','ReadWrite','None')
        $publicationLock=[IO.File]::Open("$work/site/web/.publication.lock",'OpenOrCreate','ReadWrite','None')
        $env:LAPLACE_WORK="$work/site/web/web.config/not-a-directory"
        & $command @invoke -Database application -Operation repair -MigrationRuntime "$work/site/web/releases/abc1234/migrations/Laplace.Migrations.dll" -ReceiptDirectory "$work/operator-receipts"
        $events=Get-Content $env:TEST_EVENTS
        Assert (($events | Where-Object {$_ -like 'migration:*'}) -join ',' -ceq 'migration:app-up,migration:app-check') 'Explicit runtime did not repair and check the application database.'
        $receiptFile=Get-ChildItem -LiteralPath "$work/operator-receipts" -Filter receipt.json -Recurse | Select-Object -First 1
        $receipt=Get-Content -LiteralPath $receiptFile.FullName -Raw | ConvertFrom-Json
        Assert ($receipt.completed -and $receipt.runtime_selection -eq 'explicit') 'Explicit-runtime receipt did not record completion.'
        Write-Host 'PASS explicit runtime repairs with inaccessible IIS files and a separate writable receipt directory'
    }finally{
        if($configLock){$configLock.Dispose()}
        if($publicationLock){$publicationLock.Dispose()}
        $env:LAPLACE_WORK=$originalWork
    }
    '' | Set-Content $env:TEST_EVENTS
    & $command @invoke -Database application -Operation status
    $events=Get-Content $env:TEST_EVENTS
    Assert (@($events | Where-Object {$_ -match '^sql:(GRANT|REVOKE|ALTER)' }).Count -eq 0) 'Status modified access.'
    Write-Host 'PASS status leaves database access unchanged'
    '' | Set-Content $env:TEST_EVENTS
    $env:TEST_EXTENSION_MISSING='1';$failed=$false
    try { & $command @invoke -Database substrate -Operation repair }catch{$failed=$true}
    Assert $failed 'Missing native extension was ignored.'
    Assert (@(Get-Content $env:TEST_EVENTS | Where-Object {$_ -like 'migration:*'}).Count -eq 0) 'Migration ran before extension availability was established.'
    Remove-Item Env:TEST_EXTENSION_MISSING
    Write-Host 'PASS missing substrate extension stops before database migration'
    '' | Set-Content $env:TEST_EVENTS
    $env:TEST_FAIL_VERB='app-up'
    $failed=$false
    try { & $command @invoke -Database application -Operation repair }catch{$failed=$true}
    Assert $failed 'Migration failure was ignored.'
    $events=Get-Content $env:TEST_EVENTS
    Assert (@($events | Where-Object {$_ -match '^sql:(GRANT|REVOKE|ALTER)' -or $_ -eq 'migration:app-check'}).Count -eq 0) 'Failure continued to grants or verification.'
    Remove-Item Env:TEST_FAIL_VERB
    Write-Host 'PASS failed migration stops access updates and verification'
    '' | Set-Content $env:TEST_EVENTS
    $env:TEST_OLD_RUNTIME='1'
    $failed=$false
    try { & $command @invoke -Database application -Operation recreate }catch{$failed=$true}
    Assert $failed 'A legacy runtime was accepted for application reset.'
    Assert ([string]::IsNullOrWhiteSpace((Get-Content $env:TEST_EVENTS -Raw))) 'Legacy runtime reached database access.'
    Remove-Item Env:TEST_OLD_RUNTIME
    Write-Host 'PASS legacy runtime stops before database access'
    '' | Set-Content $env:TEST_EVENTS
    $access.application_database='other'
    $access | ConvertTo-Json | Set-Content "$work/access.json"
    $failed=$false
    try { & $command @invoke -Database application -Operation recreate }catch{$failed=$true}
    Assert $failed 'Conflicting installation target was accepted.'
    Assert ([string]::IsNullOrWhiteSpace((Get-Content $env:TEST_EVENTS -Raw))) 'Conflicting target reached a database command.'
    Write-Host 'PASS conflicting targets stop before database access'
}finally{
    Remove-Item Function:global:dotnet -ErrorAction SilentlyContinue
    foreach($item in @(Get-ChildItem Env:)){if(-not $saved.ContainsKey($item.Name)){Remove-Item -LiteralPath ('Env:'+$item.Name)}}
    foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
}
