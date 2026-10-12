#Requires -Version 7
# Installed monorepo command. It does not import a checkout or compiler shell.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$arguments = [Collections.Generic.List[string]]::new()
foreach ($argument in $args) { $arguments.Add([string]$argument) }
$configPath = if ($env:LAPLACE_MONO_CLIENT_CONFIG) { $env:LAPLACE_MONO_CLIENT_CONFIG } else { Join-Path $PSScriptRoot 'client.json' }

function Absolute-Path([string]$Value, [string]$Name) {
    if (-not [IO.Path]::IsPathFullyQualified($Value)) { throw "Declare an absolute $Name path." }
    [IO.Path]::GetFullPath($Value)
}
function Read-ProfileRuntime($Profile) {
    $directory = Absolute-Path $Profile.runtime.directory 'runtime directory'
    switch ($Profile.runtime.kind) {
        'iis-release' {
            $active = Get-Content -LiteralPath (Join-Path $directory 'active.json') -Raw | ConvertFrom-Json
            if ($active.revision -notmatch '^[a-f0-9]{64}$') { throw 'Invalid installed IIS release selection.' }
            $directory = Join-Path $directory "releases/$($active.revision)/cli"
        }
        'directory' { }
        default { throw 'Runtime kind must be directory or iis-release.' }
    }
    $executable = Join-Path $directory 'Laplace.Cli.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Installed monorepo CLI is missing: $executable" }
    $executable
}
function Read-KnowledgeCredential($Credential) {
    $address = [Uri]$Credential.address
    if (-not $address.IsAbsoluteUri -or $address.Scheme -ne 'https' -or $address.UserInfo -or $address.Query -or $address.Fragment -or $address.AbsolutePath -ne '/') {
        throw 'OpenBao address must be an HTTPS origin.'
    }
    if ($Credential.record -notmatch '^kv/data/[a-zA-Z0-9_/-]+$' -or $Credential.record -match '//|/\.\.?(/|$)') { throw 'Invalid knowledge credential reference.' }
    if ($Credential.kind -ne 'openbao-dpapi-user') { throw 'Windows client requires its enrolled OpenBao identity.' }
    $secure = $null; $token = $null
    try {
        $ciphertext = Get-Content -LiteralPath (Absolute-Path $Credential.enrollment 'enrollment') -Raw
        $secure = ConvertTo-SecureString $ciphertext.Trim()
        $enrollment = ([Net.NetworkCredential]::new('', $secure)).Password | ConvertFrom-Json
        if ($enrollment.version -ne 1 -or $enrollment.client -ne $Credential.client -or $enrollment.role -ne $Credential.role -or $enrollment.address.TrimEnd('/') -ne $address.AbsoluteUri.TrimEnd('/')) {
            throw 'Client enrollment does not match this profile.'
        }
        $origin = $address.AbsoluteUri.TrimEnd('/') + '/v1/'
        $login = Invoke-RestMethod -Uri ($origin+'auth/approle/login') -Method Post -ContentType application/json -Body (@{role_id=$enrollment.role_id;secret_id=$enrollment.secret_id} | ConvertTo-Json -Compress)
        $token = $login.auth.client_token
        if (-not $token) { throw 'OpenBao did not issue a workload token.' }
        $response = Invoke-RestMethod -Uri ($origin+$Credential.record) -Headers @{'X-Vault-Token'=$token}
        return $response.data.data
    } catch {
        # Do not expose request bodies, tokens or decrypted enrollment in errors.
        throw 'Cannot read the selected knowledge credential using this Windows user enrollment. Check OpenBao reachability and the declared role/record.'
    } finally {
        if ($secure) { $secure.Dispose() }
        if ($token) {
            try { $null = Invoke-RestMethod -Uri ($origin+'auth/token/revoke-self') -Method Post -Headers @{'X-Vault-Token'=$token} -ContentType application/json -Body '{}' }
            catch { throw 'OpenBao could not revoke the temporary credential-read token; CLI was not started.' }
        }
        $enrollment = $null; $token = $null
    }
}

try {
    $config = Get-Content -LiteralPath (Absolute-Path $configPath 'client configuration') -Raw | ConvertFrom-Json -AsHashtable
    if ($config.version -ne 1) { throw 'Unsupported monorepo client configuration version.' }
    $selection = if ($env:LAPLACE_MONO_PROFILE) { $env:LAPLACE_MONO_PROFILE } else { $config.default_profile }
    if ($arguments.Count -gt 0 -and $arguments[0] -eq '--profiles') {
        foreach ($name in $config.profiles.Keys) { $p=$config.profiles[$name]; [pscustomobject]@{Profile=$name;Host=$p.knowledge.host;Database=$p.knowledge.database;Runtime=$p.runtime.directory} }
        exit 0
    }
    if ($arguments.Count -gt 0 -and $arguments[0] -eq '--profile') {
        if ($arguments.Count -lt 2) { throw 'Usage: laplace-mono --profile <name> <command> [arguments]' }
        $selection=$arguments[1]; $arguments.RemoveRange(0,2)
    }
    if (-not $selection -or -not $config.profiles.ContainsKey($selection)) { throw 'Unknown profile. Run laplace-mono --profiles.' }
    $profile=$config.profiles[$selection]
    $executable=Read-ProfileRuntime $profile
    if ($arguments.Count -gt 0 -and $arguments[0] -eq '--where') {
        [ordered]@{implementation='laplace-mono';profile=$selection;configuration=$configPath;executable=$executable;knowledge_host=$profile.knowledge.host;knowledge_database=$profile.knowledge.database;knowledge_role=$profile.knowledge.role} | ConvertTo-Json
        exit 0
    }
    $start=[Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute=$false
    $start.WorkingDirectory=(Get-Location).ProviderPath
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    foreach ($name in @('LAPLACE_DB','LAPLACE_APP_DB','PGPASSWORD','PGPASSFILE','LAPLACE_ENGINE_BUILD','LAPLACE_EXT_BUILD','LAPLACE_BUILD_ROOT','LAPLACE_PERFCACHE_BIN','LAPLACE_NATIVE_DIR')) {
        [void]$start.Environment.Remove($name)
    }
    foreach ($name in $profile.environment.Keys) { $start.Environment[$name]=[string]$profile.environment[$name] }
    $offline = $arguments.Count -eq 0 -or $arguments.Contains('--help') -or $arguments.Contains('-h') -or $arguments[0] -in @('decompose','cpu-topology')
    if (-not $offline) {
        $knowledge=$profile.knowledge
        $record=Read-KnowledgeCredential $profile.credential
        if ($record.database_host -ne $knowledge.host -or $record.database -ne $knowledge.database -or $record.role -ne $knowledge.role -or [int]$record.port -ne [int]$knowledge.port -or -not $record.password) {
            throw 'OpenBao knowledge selection differs from this profile; CLI was not started.'
        }
        $trust=Absolute-Path $knowledge.root_certificate 'PostgreSQL CA certificate'
        if (-not (Test-Path -LiteralPath $trust -PathType Leaf)) { throw 'The selected PostgreSQL trust certificate is missing.' }
        if ([int]$knowledge.pool_max -lt 1 -or [int]$knowledge.port -lt 1 -or [int]$knowledge.port -gt 65535) { throw 'Invalid database port or connection budget.' }
        $connection=[Data.Common.DbConnectionStringBuilder]::new()
        foreach ($pair in @{Host=$knowledge.host;Port=[string]$knowledge.port;Database=$knowledge.database;Username=$knowledge.role;Password=$record.password;'SSL Mode'='VerifyFull';'Root Certificate'=$trust;'Maximum Pool Size'=[string]$knowledge.pool_max}.GetEnumerator()) { $connection[$pair.Key]=$pair.Value }
        $start.Environment['LAPLACE_DB']=$connection.ConnectionString
        $record=$null; $connection=$null
    }
    $process=[Diagnostics.Process]::Start($start)
    try { $process.WaitForExit(); $result=$process.ExitCode } finally { $process.Dispose() }
    exit $result
} catch {
    [Console]::Error.WriteLine('laplace-mono: '+$_.Exception.Message)
    exit 1
}
