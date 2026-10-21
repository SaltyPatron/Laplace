# Shared data validation for setup, credential rendering and ordinary publication.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/database-connection.ps1"
function Get-IisDatabaseAuthentication($c) {
    $authentication=if($c.database.PSObject.Properties['authentication']){$c.database.authentication}else{'scram'}
    Get-LaplaceWindowsDatabaseAuthentication @{host=$c.database.host;authentication=$authentication}
}
function Get-IisCredentialReferences($c) {
    $authentication=Get-IisDatabaseAuthentication $c
    foreach($name in 'api','identity','certificate','chess','substrate','appdb'){
        if($authentication -eq 'sspi' -and $name -in @('substrate','appdb')){continue}
        $reference=$c.records.$name
        if($reference -notmatch '^kv/data/[a-zA-Z0-9_/-]+$' -or $reference -match '/\.?\./'){throw "Invalid $name credential reference."}
        [pscustomobject]@{Name=$name;Value=$reference}
    }
}
function Read-IisInstallation([string]$Path) {
    $c=Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if($c.version -ne 1 -or $c.host -ne $env:COMPUTERNAME.ToLowerInvariant()){throw 'IIS declaration version or host does not match.'}
    foreach($key in 'directory','publish','bao','machine_env') {
        if(-not [IO.Path]::IsPathFullyQualified($c.$key)){throw "Declare an absolute $key path."}
    }
    if([IO.Path]::GetFullPath($c.directory).TrimEnd('\') -eq [IO.Path]::GetPathRoot($c.directory).TrimEnd('\')){throw 'The installation cannot be a volume root.'}
    foreach($key in 'site','pool','role'){if($c.$key -notmatch '^[a-zA-Z0-9-]+$'){throw "Invalid $key."}}
    foreach($key in 'origin','address') {
        $uri=[Uri]$c.$key
        if(-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/'){throw "Declare an HTTPS origin for $key."}
    }
    $null=@(Get-IisCredentialReferences $c)
    if($c.revision -notmatch '^[a-f0-9]{7,64}$'){throw 'Declare the package revision.'}
    if($c.database.pool_max -lt 1 -or $c.database.port -lt 1 -or $c.database.port -gt 65535){throw 'Invalid database budget or port.'}
    return $c
}
function Write-IisAtomic([string]$Path,[string]$Content) {
    $temp=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.new'
    [IO.File]::WriteAllText($temp,$Content)
    [IO.File]::Move($temp,$Path,$true)
}
function Iis-Json($Value){ConvertTo-Json -InputObject $Value -Depth 16 -Compress}
function Get-IisOwnerPolicyPath($c){Join-Path $c.directory 'state/platform-owners/policy.json'}
function ConvertTo-IisCanonicalOwnerPolicy($Policy){
    $owners=@($Policy.owners | Sort-Object -Property provider,issuer,subject -CaseSensitive | ForEach-Object {
        [ordered]@{provider=$_.provider;issuer=$_.issuer;subject=$_.subject;client_id=$_.client_id;verified_at=$_.verified_at}
    })
    Iis-Json ([ordered]@{version=$Policy.version;owners=$owners})
}
function Get-IisInstallationIdentityEnvironment($c,$Identity,[switch]$IncludeSecrets){
    $path=Get-IisOwnerPolicyPath $c
    if($c.PSObject.Properties['platform_owner_policy_file']){
        if($c.platform_owner_policy_file -cne $path -or -not(Test-Path -LiteralPath $path -PathType Leaf -ErrorAction Stop)){
            throw 'The declared installed owner policy file is missing or selects another location.'
        }
    }
    if(Test-Path -LiteralPath $path -ErrorAction Stop){
        try{
            $file=Get-Item -LiteralPath $path -ErrorAction Stop
            if($file.PSIsContainer -or $file.Length -gt 65536 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'invalid file'}
            $policy=Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json -DateKind String -ErrorAction Stop
        }catch{throw 'The installed owner policy cannot be read as bounded private JSON.'}
        if($Identity.PSObject.Properties['platform_owner_policy'] -and $null -ne $Identity.platform_owner_policy -and
            (ConvertTo-IisCanonicalOwnerPolicy $Identity.platform_owner_policy) -cne (ConvertTo-IisCanonicalOwnerPolicy $policy)){throw 'Installed and credential-record owner policies disagree.'}
        $Identity=Iis-Json $Identity | ConvertFrom-Json -DateKind String
        $Identity | Add-Member platform_owner_policy $policy -Force
        $values=Get-IisIdentityEnvironment $Identity -IncludeSecrets:$IncludeSecrets
        $values.LAPLACE_PLATFORM_OWNER_POLICY_FILE=$path
        return $values
    }
    Get-IisIdentityEnvironment $Identity -IncludeSecrets:$IncludeSecrets
}
function Get-IisIdentityEnvironment($Identity,[switch]$IncludeSecrets) {
    $values=[ordered]@{}
    foreach($provider in 'google','microsoft'){
        $id=$Identity.("${provider}_client_id")
        if(-not ($id -is [string]) -or -not $id -or $id -match '[\x00-\x1f]'){throw 'Invalid provider client configuration.'}
        $values["LAPLACE_AUTH_$($provider.ToUpperInvariant())_CLIENT_ID"]=$id
        if($IncludeSecrets){
            $secret=$Identity.("${provider}_client_secret")
            if(-not ($secret -is [string]) -or -not $secret -or $secret -match '[\x00-\x1f]'){throw 'Invalid provider secret configuration.'}
            $values["LAPLACE_AUTH_$($provider.ToUpperInvariant())_CLIENT_SECRET"]=$secret
        }
    }
    if($Identity.PSObject.Properties['platform_owner_policy'] -and $null -ne $Identity.platform_owner_policy){
        $policy=$Identity.platform_owner_policy
        if($policy.version -cne 1 -or -not ($policy.owners -is [array]) -or $policy.owners.Count -gt 16){throw 'Invalid owner policy contract.'}
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($owner in $policy.owners){
            if($owner.provider -cnotin @('google','microsoft')){throw 'Invalid owner policy provider.'}
            foreach($field in 'issuer','subject','client_id','verified_at'){
                $value=$owner.$field
                if(-not ($value -is [string]) -or [string]::IsNullOrWhiteSpace($value) -or $value.Length -gt 4096 -or $value -match '[\x00-\x1f]'){throw 'Invalid owner policy binding.'}
            }
            if($owner.client_id -cne $Identity.("$($owner.provider)_client_id")){throw 'Owner policy belongs to a different provider client.'}
            $verified=[DateTimeOffset]::MinValue
            if($owner.verified_at -notmatch '(Z|[+-]\d\d:\d\d)$' -or -not [DateTimeOffset]::TryParse($owner.verified_at,[ref]$verified) -or $verified -gt [DateTimeOffset]::UtcNow){throw 'Invalid owner policy verification timestamp.'}
            $binding=Iis-Json @($owner.provider,$owner.issuer,$owner.subject)
            if(-not $seen.Add($binding)){throw 'Duplicate owner policy binding.'}
        }
        $json=Iis-Json $policy
        if([Text.Encoding]::UTF8.GetByteCount($json) -gt 65536){throw 'Owner policy exceeds its size limit.'}
        $values.LAPLACE_PLATFORM_OWNER_POLICY=$json
    }
    return $values
}
function Get-IisChessAccess($c) {
    if($c.chess.match_engines){
        $connector=$c.chess.match_engines.connector
        if(-not [IO.Path]::IsPathFullyQualified($connector) -or -not(Test-Path -LiteralPath $connector -PathType Leaf)){
            throw 'Match engines require an existing absolute connector executable.'
        }
        [pscustomobject]@{Path=(Split-Path -Parent $connector);Rights='ReadAndExecute';Directory=$true}
    }
    $file=$c.environment.LAPLACE_CHESS_ENV
    if(-not $file){return}
    if(-not [IO.Path]::IsPathFullyQualified($file) -or -not(Test-Path -LiteralPath $file -PathType Leaf)){throw 'LAPLACE_CHESS_ENV must name an existing absolute public configuration file.'}
    [pscustomobject]@{Path=$file;Rights='ReadAndExecute';Directory=$false}
    $executables=@('LAPLACE_STOCKFISH','LAPLACE_CUTECHESS','LAPLACE_CUTECHESS_GUI','LAPLACE_FASTCHESS','LAPLACE_ORDO','LAPLACE_LC0','LAPLACE_UCI','LAPLACE_ZSTD_LIBRARY')
    $files=@('LAPLACE_LC0_NET','LAPLACE_CUTECHESS_GUI_RECEIPT')
    $directories=@('LAPLACE_DATA','LAPLACE_SYZYGY','LAPLACE_CHESS_OPENINGS','LAPLACE_CHESS_BOOKS','LAPLACE_QT_BIN')
    foreach($line in [IO.File]::ReadLines($file)){
        if($line.TrimStart().StartsWith('#') -or -not $line.Contains('=')){continue}
        $key,$value=$line.Split('=',2);$key=$key.Trim();$value=$value.Trim().Trim('"',"'")
        if($key -match 'TOKEN|SECRET|PASSWORD|API_KEY'){throw 'The shared chess configuration must not contain credentials.'}
        if($key -notin ($executables+$files+$directories+@('LAPLACE_CHESS_LAB_DIR'))){continue}
        if(-not [IO.Path]::IsPathFullyQualified($value)){throw "Declare an absolute $key path."}
        if($key -eq 'LAPLACE_CHESS_LAB_DIR'){
            if([IO.Path]::GetFullPath($value).TrimEnd('\') -eq [IO.Path]::GetPathRoot($value).TrimEnd('\')){throw 'Chess job output cannot be a volume root.'}
            [pscustomobject]@{Path=$value;Rights='Modify';Directory=$true}
        }else{
            $kind=if($key -in $directories){'Container'}else{'Leaf'}
            if(-not(Test-Path -LiteralPath $value -PathType $kind)){throw "Declared $key is missing: $value"}
            $path=if($key -in $executables){Split-Path -Parent $value}else{$value}
            [pscustomobject]@{Path=$path;Rights='ReadAndExecute';Directory=($key -notin $files)}
        }
    }
}
function Get-IisPageAssets([string]$Html) {
    @([regex]::Matches($Html,'(?:src|href)="(/assets/[^"]+)"') | ForEach-Object {$_.Groups[1].Value} | Select-Object -Unique)
}
function Test-IisChessCacheClosure([string]$Path) {
    foreach($name in 'laplace_chess_position_perfcache.bin','laplace_chess_transition_perfcache.bin') {
        $expected=$null
        foreach($component in @('','cli','uci')) {
            $file=Join-Path (Join-Path $Path $component) ('share/laplace/'+$name)
            if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw "Incomplete IIS chess cache: $file"}
            $digest=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
            if($expected -and $digest -ne $expected){throw "IIS chess cache differs between application components: $name ($component)"}
            $expected=$digest
        }
    }
}
function Test-IisPackage([string]$Path) {
    Test-IisChessCacheClosure $Path
    foreach($file in 'migrations/Laplace.Migrations.dll','migrations/Laplace.Migrations.deps.json','migrations/Laplace.Migrations.runtimeconfig.json') {
        if(-not(Test-Path -LiteralPath (Join-Path $Path $file) -PathType Leaf)){throw "Incomplete IIS migration runtime: $file"}
    }
    if(-not @(Get-ChildItem -LiteralPath (Join-Path $Path 'migrations/db/migrations') -Filter '*.sql' -File -ErrorAction SilentlyContinue).Count){throw 'The IIS migration runtime has no SQL migrations.'}
    foreach($file in @('Laplace.Endpoints.OpenAICompat.exe','Laplace.Endpoints.OpenAICompat.dll','Laplace.Endpoints.OpenAICompat.runtimeconfig.json','wwwroot/index.html','laplace_core.dll','laplace_dynamics.dll','laplace_synthesis.dll','laplace_syzygy.dll','share/laplace/laplace_t0_perfcache.bin','cli/Laplace.Cli.exe')) {
        if(-not(Test-Path -LiteralPath (Join-Path $Path $file) -PathType Leaf)){throw "Incomplete IIS package: $file"}
    }
    $manifest=Get-Content (Join-Path $Path 'iis-package.json') -Raw | ConvertFrom-Json
    if(-not $manifest.files -or $manifest.revision -notmatch '^[a-f0-9]{7,64}$'){throw 'Incomplete package manifest.'}
    if($manifest.components -contains 'uci'){
        foreach($file in 'laplace-uci.exe','laplace-uci.dll','laplace-uci.deps.json','laplace-uci.runtimeconfig.json','laplace_core.dll','laplace_syzygy.dll','laplace_dynamics.dll','laplace_synthesis.dll','share/laplace/laplace_t0_perfcache.bin','share/laplace/laplace_highway_perfcache.bin','stockfish-profiles.json','lc0-profiles.json','chess-ladder-release.json','cutechess-release.json','stockfish-release.json','zstd-release.json'){
            if(-not(Test-Path -LiteralPath (Join-Path $Path ('uci/'+$file)) -PathType Leaf)){throw "Incomplete IIS chess runtime: uci/$file"}
        }
    }
    if(Test-Path -LiteralPath (Join-Path $Path 'web.config')){throw 'The package contains a host-specific web.config; use New-IisPackage.ps1.'}
    $listed=@($manifest.files.path)
    foreach($required in 'Laplace.Endpoints.OpenAICompat.exe','Laplace.Endpoints.OpenAICompat.dll','cli\Laplace.Cli.exe'){
        if($required -notin $listed){throw "Untracked required package file: $required"}
    }
    $actual=@(Get-ChildItem -LiteralPath $Path -File -Recurse | Where-Object Name -NE 'iis-package.json' |
        ForEach-Object {[IO.Path]::GetRelativePath($Path,$_.FullName)})
    $listedSet=[Collections.Generic.HashSet[string]]::new([string[]]$listed,[StringComparer]::OrdinalIgnoreCase)
    $actualSet=[Collections.Generic.HashSet[string]]::new([string[]]$actual,[StringComparer]::OrdinalIgnoreCase)
    if($listedSet.Count -ne $listed.Count){throw 'The package manifest contains duplicate file paths.'}
    $missing=@($listed | Where-Object {-not $actualSet.Contains($_)})
    $unlisted=@($actual | Where-Object {-not $listedSet.Contains($_)})
    if($missing.Count -or $unlisted.Count){
        throw "Package files differ from the manifest. Missing ($($missing.Count)): $(($missing | Select-Object -First 20) -join ', '); unlisted ($($unlisted.Count)): $(($unlisted | Select-Object -First 20) -join ', ')."
    }
    $assets=Get-IisPageAssets (Get-Content -LiteralPath (Join-Path $Path 'wwwroot/index.html') -Raw)
    if(-not $assets){throw 'The UI entry point has no compiled asset references.'}
    foreach($asset in $assets){
        if(-not(Test-Path -LiteralPath (Join-Path $Path ('wwwroot/'+$asset.TrimStart('/'))) -PathType Leaf)){throw "Missing compiled UI asset: $asset"}
    }
    foreach($file in $manifest.files) {
        $target=[IO.Path]::GetFullPath((Join-Path $Path $file.path))
        if(-not $target.StartsWith([IO.Path]::GetFullPath($Path).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Package member escapes the package.'}
        if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256){throw "Package digest mismatch: $($file.path)"}
    }
    return $manifest
}
function Get-IisEnvironment($c,$records) {
    $state=Join-Path $c.directory 'state'
    $values=[ordered]@{}
    foreach($item in $c.environment.PSObject.Properties){
        if($item.Name -notmatch '^(LAPLACE_|ASPNETCORE_|DOTNET_|OMP_|MKL_)' -or $item.Name -match 'TOKEN|SECRET|PASSWORD|AUTH_DEV_PRINCIPAL|API_KEY|PLATFORM_OWNER_POLICY'){throw 'Public environment must contain only non-secret runtime configuration.'}
        $values[$item.Name]=[string]$item.Value
    }
    foreach($item in @(@('substrate','substrate_database','substrate_role','LAPLACE_DB'),@('appdb','application_database','application_role','LAPLACE_APP_DB'))) {
        $authentication=Get-IisDatabaseAuthentication $c
        $selection=@{host=$c.database.host;port=$c.database.port;database=$c.database.($item[1]);role=$c.database.($item[2]);pool_max=$c.database.pool_max;authentication=$authentication;root_certificate=(Join-Path $state 'postgres-root.crt')}
        $record=if($authentication -eq 'sspi'){$null}else{$records.($item[0])}
        if($authentication -eq 'scram' -and $record.password -notmatch '^[a-f0-9]{64}$'){throw 'Invalid database credential contract.'}
        $values[$item[3]]=New-LaplaceWindowsDatabaseConnection $selection $record
    }
    if($records.api.operator_token -notmatch '^[a-f0-9]{64}$'){throw 'Missing runtime operator credential.'}
    $values.LAPLACE_OPERATOR_TOKEN=$records.api.operator_token
    $identityValues=Get-IisInstallationIdentityEnvironment $c $records.identity -IncludeSecrets
    # The API reads the canonical file at each startup; do not retain an older
    # JSON value in its pool environment after an ordinary operator update.
    if($identityValues.Contains('LAPLACE_PLATFORM_OWNER_POLICY_FILE')){$identityValues.Remove('LAPLACE_PLATFORM_OWNER_POLICY')}
    foreach($key in $identityValues.Keys){$values[$key]=$identityValues[$key]}
    $values.LAPLACE_PUBLIC_BASE_URL=$c.origin.TrimEnd('/')
    $values.LAPLACE_AUTH_MODE='identity';$values.LAPLACE_BILLING_STORE='postgres'
    $values.LAPLACE_DATA_PROTECTION_KEYS=Join-Path $state 'keys'
    $values.LAPLACE_OPS_LOG_DIR=Join-Path $state 'logs'
    $values.LAPLACE_LICHESS_CONTROL_CLIENT=Join-Path $state 'chess-client.json'
    if($c.chess.match_engines){
        $values.LAPLACE_CHESS_UCI_CONNECTOR=$c.chess.match_engines.connector
        $values.LAPLACE_CHESS_UCI_CONFIG=Join-Path $state 'engine-connections/connector.json'
    }
    return $values
}
function Get-IisWebConfig([string]$Revision,[string]$LogDirectory) {
    $log=[Security.SecurityElement]::Escape((Join-Path $LogDirectory 'iis'))
    return '<configuration><system.webServer><handlers><add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" /></handlers><aspNetCore processPath=".\releases\'+$Revision+'\Laplace.Endpoints.OpenAICompat.exe" hostingModel="inprocess" stdoutLogEnabled="true" stdoutLogFile="'+$log+'" /></system.webServer></configuration>'
}
function Test-IisEndpoint($c,[string]$OperatorToken='',[string]$ApiKey='',[string]$Base='') {
    $base=if($Base){$Base.TrimEnd('/')}else{$c.origin.TrimEnd('/')}
    $health=Invoke-RestMethod "$base/health" -NoProxy -TimeoutSec 20
    if($health.status -ne 'ok'){throw 'IIS application liveness failed.'}
    $ready=Invoke-WebRequest "$base/health/ready" -NoProxy -TimeoutSec 20 -SkipHttpErrorCheck
    $state=$ready.Content | ConvertFrom-Json
    if($state.substrate_reachable -ne $true -or $state.perfcache_ready -ne $true){throw 'IIS substrate or native perfcache is unavailable.'}
    $page=Invoke-WebRequest $base -NoProxy -TimeoutSec 20
    if($page.Content -notmatch '<html'){throw 'The installed UI was not served.'}
    $assets=Get-IisPageAssets $page.Content
    if(-not $assets){throw 'The UI entry point has no compiled asset references.'}
    foreach($asset in $assets){
        $response=Invoke-WebRequest ($base+$asset) -NoProxy -TimeoutSec 20
        if(-not $response.RawContentLength -or [string]$response.Headers['Content-Type'] -match 'text/html'){throw "Invalid compiled UI asset response: $asset"}
    }
    $auth=Invoke-RestMethod "$base/v1/auth/config" -NoProxy -TimeoutSec 20
    foreach($name in 'google','microsoft'){if($name -notin @($auth.providers.id)){throw "IIS $name authentication is not configured."}}
    if(-not $OperatorToken -and -not $ApiKey){throw 'Authenticated verification requires an operator credential or a workload API key.'}
    $issued=$null
    try {
        if(-not $ApiKey){
            $issued=Invoke-RestMethod "$base/v1/billing/operator/keys" -Method Post -ContentType application/json -Body '{"tenant":"deployment-verification","label":"iis-installed-runtime-check"}' -Headers @{'X-Laplace-Operator-Token'=$OperatorToken} -NoProxy -TimeoutSec 20
            $ApiKey=$issued.api_key
            if($ApiKey -notlike 'sk-laplace-*' -or -not $issued.key_prefix){throw 'Verification key issuance failed.'}
        }
        $headers=@{Authorization="Bearer $ApiKey"}
        $op=Invoke-RestMethod "$base/v1/op" -Method Post -ContentType application/json -Body '{"name":"ops.substrate_counts","max_rows":20}' -Headers $headers -NoProxy -TimeoutSec 20
        if($op.object -ne 'op.result' -or $op.name -ne 'ops.substrate_counts'){throw 'Typed substrate operation returned the wrong contract.'}
        $body='{"text":"Laplace deployment validation."}'
        $proof=Invoke-RestMethod "$base/v1/explore/storage-proof" -Method Post -ContentType application/json -Body $body -Headers $headers -NoProxy -TimeoutSec 20
        if($proof.perfcache_aligned -ne $true -or -not $proof.perfcache_receipt_hex -or $proof.perfcache_receipt_hex -ne $proof.database_perfcache_receipt_hex -or $proof.database_perfcache_error){throw 'API and PostgreSQL native artifacts disagree.'}
        foreach($name in 'tier0_rom','merkle_recomposition','trajectory_roundtrip'){
            if(-not @($proof.invariants | Where-Object {$_.key -eq $name -and $_.passed -eq $true}).Count){throw "Native storage invariant failed: $name"}
        }
        if(@($proof.invariants | Where-Object {$_.passed -ne $true}).Count){throw 'Native storage proof contains a failed invariant.'}
        $decomposition=Invoke-RestMethod "$base/v1/explore/decompose" -Method Post -ContentType application/json -Body $body -Headers $headers -NoProxy -TimeoutSec 20
        if(-not $proof.root_id_hex -or $proof.root_id_hex -ne $decomposition.root_id_hex){throw 'Native operations disagree on canonical identity.'}
        $chess=Invoke-RestMethod "$base/chess/lichess/control" -Headers $headers -NoProxy -TimeoutSec 20
        if($chess.available -ne $true){throw 'The declared Lichess controller is unavailable through this application.'}
    } finally {
        if($issued -and $issued.key_prefix){
            $null=Invoke-RestMethod "$base/v1/billing/keys/revoke" -Method Post -ContentType application/json -Body (Iis-Json @{key_prefix=$issued.key_prefix}) -Headers @{Authorization="Bearer $ApiKey"} -NoProxy -TimeoutSec 20
        }
    }
    $redirects=@{}
    if($base -eq $c.origin.TrimEnd('/')){
        $handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false;$handler.UseProxy=$false
        $client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[TimeSpan]::FromSeconds(20)
        try{
            foreach($provider in 'google','microsoft'){
                $response=$client.GetAsync("$base/v1/auth/login/$provider").GetAwaiter().GetResult()
                try{
                    $location=[string]$response.Headers.Location
                    if([int]$response.StatusCode -ne 302 -or $location -notmatch '[?&]redirect_uri=([^&]+)'){throw "$provider did not issue its authorization redirect."}
                    $callback=[Net.WebUtility]::UrlDecode($Matches[1])
                    if($callback -ne "$base/signin-$provider"){throw "$provider callback differs from the declared origin."}
                    $redirects[$provider]=$callback
                }finally{$response.Dispose()}
            }
        }finally{$client.Dispose();$handler.Dispose()}
    }
    return @{verified_utc=[DateTime]::UtcNow.ToString('o');origin=$base;health='passed';substrate='passed';perfcache='passed';ui='passed';ui_entry_assets=$assets.Count;providers=@($auth.providers.id);callbacks=$redirects;authenticated_sql='passed';native_storage='passed';lichess_controller='passed';product_ready=$state.ready}
}
