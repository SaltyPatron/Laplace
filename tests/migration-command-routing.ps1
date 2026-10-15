#Requires -Version 7
param([Parameter(Mandatory)][string]$Runtime)
$ErrorActionPreference='Stop'
$savedDb=$env:LAPLACE_DB;$savedApp=$env:LAPLACE_APP_DB
try {
    $env:LAPLACE_DB='Host=127.0.0.1;Port=1;Database=substrate;Username=fixture;Timeout=1'
    Remove-Item Env:LAPLACE_APP_DB -ErrorAction SilentlyContinue
    foreach($verb in 'app-up','app-status','app-check','app-reset','app-nuke'){
        $output=& dotnet $Runtime $verb --yes 2>&1
        if($LASTEXITCODE -eq 0 -or ($output -join "`n") -notlike '*requires LAPLACE_APP_DB*'){throw "$verb did not reject a missing application target: $output"}
    }
    $env:LAPLACE_APP_DB=$env:LAPLACE_DB
    foreach($verb in 'app-reset','app-nuke'){
        $output=& dotnet $Runtime $verb --yes 2>&1
        if($LASTEXITCODE -eq 0 -or ($output -join "`n") -notlike '*requires distinct application and substrate*'){throw "$verb accepted a shared database: $output"}
    }
    $env:LAPLACE_APP_DB='Host=127.0.0.1;Port=1;Database=accounts;Username=fixture;Timeout=1'
    $output=& dotnet $Runtime app-nuke --database substrate --yes 2>&1
    if($LASTEXITCODE -eq 0 -or ($output -join "`n") -notlike '*conflicting target override*'){throw 'Application target override was accepted.'}
    $output=& dotnet $Runtime app-recreate --yes 2>&1
    if($LASTEXITCODE -ne 2 -or ($output -join "`n") -notlike '*Unknown migration command*'){throw 'Unknown verb was not rejected before database access.'}
    Write-Host 'PASS compiled migration commands reject missing targets, shared-database resets, conflicting overrides and unknown verbs'
}finally{$env:LAPLACE_DB=$savedDb;$env:LAPLACE_APP_DB=$savedApp}
