using System.Text.Json;
using System.Text.Json.Nodes;
using Laplace.Engine.Core;

namespace Laplace.Agents;

/// <summary>One named agent from <c>agents.json</c>: a provider, a model, and the knobs that ride with them.</summary>
public sealed record AgentDefinition(
    string Name,
    string Provider,
    string? Model = null,
    string? BaseUrl = null,
    string? ApiKeyEnv = null,
    int? MaxTokens = null,
    double? Temperature = null,
    string? System = null,
    AgentAuth? Auth = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? TokenCommand = null);

/// <summary>A fully resolved call target — nothing left to look up, nothing left to guess.</summary>
public sealed record AgentTarget(
    string Name,
    AgentProvider Provider,
    string Model,
    string BaseUrl,
    string? ApiKey,
    int? MaxTokens,
    double? Temperature,
    string? System,
    // No default. A defaulted auth mode silently places the credential on the
    // wrong header for any provider whose default is not Bearer, and the result
    // is a correct request rejected as unauthenticated — so every construction
    // states it.
    AgentAuth Auth,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>What <c>agents</c> reports: a route the caller can name, and whether it can actually be called.</summary>
public sealed record AgentDescriptor(
    string Name,
    string Provider,
    string? Model,
    string BaseUrl,
    string KeyEnv,
    bool Credentialed,
    bool IsAlias,
    bool IsDefault,
    string Auth,
    string CredentialSource);

/// <summary>
/// Resolves an agent reference — an alias, a <c>provider/model</c> pair, or a bare
/// vendor-branded model id — into a callable <see cref="AgentTarget"/>.
///
/// THREE CONFIG SOURCES, ONE PRECEDENCE. Keys come from the process environment
/// first and then <c>secrets/agents.env</c> via <see cref="LaplaceInstall.TryReadConfig"/> —
/// the same lane the API service already uses for Stripe and Lichess, so the MCP
/// server finds credentials the host has even though an agent client launches it
/// over stdio with an empty environment and no way to inject one. Aliases and
/// base-URL overrides come from <c>agents.json</c>. THAT FILE NEVER HOLDS A SECRET:
/// it names the environment variable to read (<c>api_key_env</c>) and nothing else,
/// because the deploy syncs it into /opt/laplace/app where the API's own env file
/// is deliberately excluded from the payload.
///
/// The catalog is re-read per call. An operator editing agents.json must not have
/// to find and restart a process that is a stdio child of whatever client spawned
/// it — the same reason <c>op</c> resolves against the live SQL catalog (GH #809).
/// </summary>
public sealed class AgentCatalog
{
    private readonly Dictionary<string, AgentDefinition> _aliases;
    private readonly Dictionary<string, (string? BaseUrl, string? ApiKeyEnv)> _providerOverrides;
    private readonly string? _configuredDefault;
    private readonly Func<string, string?> _env;

    public string? ConfigPath { get; }

    private AgentCatalog(
        Dictionary<string, AgentDefinition> aliases,
        Dictionary<string, (string?, string?)> providerOverrides,
        string? configuredDefault,
        string? configPath,
        Func<string, string?> env)
    {
        _aliases = aliases;
        _providerOverrides = providerOverrides;
        _configuredDefault = configuredDefault;
        _env = env;
        ConfigPath = configPath;
    }

    /// <summary>The process-wide catalog: discovered config file plus the env/secret-file reader.</summary>
    public static AgentCatalog Load() => Load(DiscoverConfigPath(), DefaultEnvReader);

    public static string? DefaultEnvReader(string key) =>
        LaplaceInstall.TryReadConfig(key, AgentProviders.SecretFile);

    public static AgentCatalog Load(string? configPath, Func<string, string?> env)
    {
        string? json = null;
        if (configPath is not null && File.Exists(configPath))
        {
            try { json = File.ReadAllText(configPath); }
            catch (IOException ex) { throw new AgentException($"agents config unreadable at {configPath}: {ex.Message}", ex); }
        }
        else
        {
            configPath = null;
        }

        return Parse(json, configPath, env);
    }

    /// <summary>Parse a config document without touching the filesystem.</summary>
    public static AgentCatalog Parse(string? json, string? configPath, Func<string, string?> env)
    {
        var aliases = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);
        var overrides = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        string? configuredDefault = null;

        if (!string.IsNullOrWhiteSpace(json))
        {
            JsonNode? root;
            try { root = JsonNode.Parse(json!); }
            catch (JsonException ex)
            {
                throw new AgentException(
                    $"agents config is not valid JSON{(configPath is null ? "" : $" ({configPath})")}: {ex.Message}", ex);
            }

            configuredDefault = Str(root?["default"]);

            if (root?["agents"] is JsonObject agents)
            {
                foreach (var (name, node) in agents)
                {
                    if (node is not JsonObject entry)
                        throw new AgentException($"agents config: '{name}' must be an object");

                    var provider = Str(entry["provider"])
                        ?? throw new AgentException($"agents config: '{name}' needs a \"provider\"");
                    if (!AgentProviders.TryGet(provider, out _))
                        throw new AgentException(
                            $"agents config: '{name}' names unknown provider '{provider}'. Installed: " +
                            string.Join(", ", AgentProviders.All.Select(p => p.Id)));

                    if (entry["api_key"] is not null)
                        throw new AgentException(
                            $"agents config: '{name}' carries an inline \"api_key\". Secrets never live in this " +
                            "file — it is world-readable in the deployed app directory. Use \"api_key_env\" and " +
                            $"put the value in the process environment or secrets/{AgentProviders.SecretFile}.");

                    aliases[name] = new AgentDefinition(
                        name,
                        provider,
                        Str(entry["model"]),
                        Str(entry["base_url"]),
                        Str(entry["api_key_env"]),
                        Num(entry["max_tokens"]) is { } mt ? (int)mt : null,
                        Num(entry["temperature"]),
                        Str(entry["system"]),
                        ParseAuth(name, Str(entry["auth"])),
                        ParseHeaders(name, entry["headers"]),
                        Str(entry["token_command"]));
                }
            }

            if (root?["providers"] is JsonObject providers)
            {
                foreach (var (id, node) in providers)
                {
                    if (!AgentProviders.TryGet(id, out _))
                        throw new AgentException($"agents config: providers.'{id}' is not an installed provider");
                    overrides[id] = (Str(node?["base_url"]), Str(node?["api_key_env"]));
                }
            }
        }

        return new AgentCatalog(aliases, overrides, configuredDefault, configPath, env);
    }

    /// <summary>
    /// Where the config is looked for, first hit wins: an explicit
    /// LAPLACE_AGENTS_CONFIG, the deployed app directory, the repo's config/, then
    /// the per-user XDG location.
    ///
    /// An explicit path that does not exist is an ERROR, not a fall-through. The
    /// silent version of this loses every alias and reports the catalog as merely
    /// empty, which reads as "my agents disappeared" rather than "that path has a
    /// typo" — and the fall-through would then route the call to a different model
    /// than the one the operator configured.
    /// </summary>
    public static string? DiscoverConfigPath() => DiscoverConfigPath(Environment.GetEnvironmentVariable);

    public static string? DiscoverConfigPath(Func<string, string?> env)
    {
        var explicitPath = env("LAPLACE_AGENTS_CONFIG");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath.Trim());
            return File.Exists(full)
                ? full
                : throw new AgentException(
                    $"LAPLACE_AGENTS_CONFIG points at {full}, which does not exist. Unset it to fall back to " +
                    "the default search, or correct the path.");
        }

        foreach (var candidate in ConfigCandidates(env))
            if (File.Exists(candidate))
                return candidate;
        return null;
    }

    public static IEnumerable<string> ConfigCandidates() =>
        ConfigCandidates(Environment.GetEnvironmentVariable);

    public static IEnumerable<string> ConfigCandidates(Func<string, string?> env)
    {
        var explicitPath = env("LAPLACE_AGENTS_CONFIG");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            yield return Path.GetFullPath(explicitPath.Trim());

        var appDir = env("LAPLACE_APP_DIR");
        if (string.IsNullOrWhiteSpace(appDir) && !OperatingSystem.IsWindows())
            appDir = "/opt/laplace/app";
        if (!string.IsNullOrWhiteSpace(appDir))
            yield return Path.Combine(appDir.Trim(), "agents.json");

        if (LaplaceInstall.TryRepoRoot(out var root))
            yield return Path.Combine(root, "config", "agents.json");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, ".config", "laplace", "agents.json");
    }

    /// <summary>
    /// Every route the caller can name, with a credential verdict per row. Never
    /// carries a key value — only the variable name that would supply one, so this
    /// is safe to return to a model.
    /// </summary>
    public IReadOnlyList<AgentDescriptor> Describe()
    {
        var defaultRef = DefaultReference();
        var rows = new List<AgentDescriptor>();

        foreach (var (name, def) in _aliases.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var provider = AgentProviders.Get(def.Provider);
            // DESCRIBE NEVER RUNS THE TOKEN COMMAND. This surface is polled by a UI
            // and read by models; spawning an auth subprocess per row per refresh
            // would turn an inventory into a login storm.
            var minted = !string.IsNullOrWhiteSpace(def.TokenCommand);
            var (key, keyEnv) = minted ? (null, "token_command") : ResolveKey(provider, def);
            rows.Add(new AgentDescriptor(
                name, provider.Id, def.Model ?? provider.DefaultModel,
                ResolveBaseUrl(provider, def.BaseUrl), keyEnv,
                minted || key is not null || !provider.RequiresKey,
                IsAlias: true,
                IsDefault: string.Equals(name, defaultRef, StringComparison.OrdinalIgnoreCase),
                Auth: (def.Auth ?? provider.Auth).ToString().ToLowerInvariant(),
                CredentialSource: minted ? $"token_command: {def.TokenCommand}" : $"env: {keyEnv}"));
        }

        foreach (var provider in AgentProviders.All)
        {
            var (key, keyEnv) = ResolveKey(provider, definition: null);
            rows.Add(new AgentDescriptor(
                provider.Id, provider.Id, provider.DefaultModel,
                ResolveBaseUrl(provider, null), keyEnv,
                key is not null || !provider.RequiresKey,
                IsAlias: false,
                IsDefault: string.Equals(provider.Id, defaultRef, StringComparison.OrdinalIgnoreCase),
                Auth: provider.Auth.ToString().ToLowerInvariant(),
                CredentialSource: $"env: {keyEnv}"));
        }

        return rows;
    }

    /// <summary>The reference used when a call names no model: env first, then the config file's "default".</summary>
    public string? DefaultReference()
    {
        var fromEnv = _env("LAPLACE_AGENT_DEFAULT");
        return string.IsNullOrWhiteSpace(fromEnv) ? _configuredDefault : fromEnv.Trim();
    }

    /// <summary>
    /// Resolve a reference to a callable target.
    ///
    /// <paramref name="providerId"/> FORCES THE ROUTE: when it is given, the alias
    /// table is skipped entirely and <paramref name="modelRef"/> is read as the
    /// vendor's own model id. Overlaying an explicit provider onto an alias that
    /// already names one would make 'which provider ran this' unanswerable from the
    /// arguments alone, and that is the question a bill is settled with.
    /// </summary>
    public AgentTarget Resolve(string? modelRef, string? providerId = null)
    {
        var reference = string.IsNullOrWhiteSpace(modelRef) ? null : modelRef!.Trim();

        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var forced = AgentProviders.Get(providerId!);
            return Build(forced, reference, definition: null, name: reference is null ? forced.Id : $"{forced.Id}/{reference}");
        }

        if (reference is null)
        {
            reference = DefaultReference();
            if (reference is null)
            {
                var credentialed = Describe().Where(d => d.Credentialed && d.IsAlias == false && d.Model is not null).ToList();
                throw new AgentException(
                    "no model given and no default configured. Pass model (an alias, 'provider/model', or a " +
                    "vendor-branded name), or set LAPLACE_AGENT_DEFAULT, or add \"default\" to agents.json. " +
                    (credentialed.Count > 0
                        ? "Credentialed providers with a known default model: " +
                          string.Join(", ", credentialed.Select(d => $"{d.Provider}/{d.Model}"))
                        : "No provider currently has both credentials and a known default model — call the " +
                          "`agents` tool to see which keys are missing."));
            }
        }

        if (_aliases.TryGetValue(reference, out var alias))
            return Build(AgentProviders.Get(alias.Provider), alias.Model, alias, alias.Name);

        var slash = reference.IndexOf('/');
        if (slash > 0 && AgentProviders.TryGet(reference[..slash], out var qualified))
        {
            // Split once only: OpenRouter's own ids are vendor/model, so
            // 'openrouter/anthropic/claude-x' must keep 'anthropic/claude-x' intact.
            var model = reference[(slash + 1)..].Trim();
            if (model.Length == 0)
                throw new AgentException($"'{reference}' names a provider with no model after the slash");
            return Build(qualified, model, definition: null, name: reference);
        }

        var inferred = AgentProviders.InferFromModelName(reference);
        if (inferred is not null)
            return Build(inferred, reference, definition: null, name: $"{inferred.Id}/{reference}");

        throw new AgentException(
            $"cannot route '{reference}': it is not a configured alias, it does not start with a provider id, " +
            "and its name carries no vendor prefix this table recognises. Qualify it as 'provider/model' " +
            $"(installed: {string.Join(", ", AgentProviders.All.Select(p => p.Id))}), pass the provider argument, " +
            "or define it as an alias in agents.json.");
    }

    private AgentTarget Build(AgentProvider provider, string? model, AgentDefinition? definition, string name)
    {
        var resolvedModel = model ?? definition?.Model ?? provider.DefaultModel
            ?? throw new AgentException(
                $"provider '{provider.Id}' has no default model — name one as '{provider.Id}/<model>' or " +
                "define an alias with a \"model\" in agents.json.");

        var baseUrl = ResolveBaseUrl(provider, definition?.BaseUrl);
        if (baseUrl.Length == 0)
            throw new AgentException(
                $"provider '{provider.Id}' has no base URL. Set \"base_url\" on the agent (or on " +
                $"providers.{provider.Id}) in agents.json.");

        // A token_command outranks every variable: the operator who configured one
        // is saying this credential expires and must be minted, and reading a stale
        // env var instead would be the failure the command exists to prevent.
        string? key;
        string keyEnv;
        if (!string.IsNullOrWhiteSpace(definition?.TokenCommand))
        {
            key = TokenCommand.Run(definition!.TokenCommand!);
            keyEnv = "token_command";
        }
        else
        {
            (key, keyEnv) = ResolveKey(provider, definition);
        }

        if (key is null && provider.RequiresKey)
            throw new AgentException(
                $"no credential for provider '{provider.Id}': set {keyEnv} in the environment or in " +
                $"secrets/{AgentProviders.SecretFile}, or give the agent a token_command that mints one.");

        return new AgentTarget(
            name, provider, resolvedModel, baseUrl, key,
            definition?.MaxTokens, definition?.Temperature, definition?.System,
            definition?.Auth ?? provider.Auth,
            definition?.Headers);
    }

    private string ResolveBaseUrl(AgentProvider provider, string? fromDefinition)
    {
        if (!string.IsNullOrWhiteSpace(fromDefinition))
            return fromDefinition!.TrimEnd('/');

        if (_providerOverrides.TryGetValue(provider.Id, out var ov) && !string.IsNullOrWhiteSpace(ov.BaseUrl))
            return ov.BaseUrl!.TrimEnd('/');

        // The self-route points at whatever this install serves, not a constant.
        if (provider.Id == "laplace")
            return $"{LaplaceInstall.EndpointBaseUrl}/v1";

        return provider.DefaultBaseUrl.TrimEnd('/');
    }

    /// <summary>The key, and the variable name it was looked for under (reported even when unset).</summary>
    private (string? Key, string KeyEnv) ResolveKey(AgentProvider provider, AgentDefinition? definition)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(definition?.ApiKeyEnv)) names.Add(definition!.ApiKeyEnv!.Trim());
        if (_providerOverrides.TryGetValue(provider.Id, out var ov) && !string.IsNullOrWhiteSpace(ov.ApiKeyEnv))
            names.Add(ov.ApiKeyEnv!.Trim());
        names.AddRange(provider.ApiKeyEnvNames);

        foreach (var n in names)
        {
            var v = _env(n);
            if (!string.IsNullOrWhiteSpace(v)) return (v.Trim(), n);
        }

        return (null, names[0]);
    }

    private static AgentAuth? ParseAuth(string name, string? value) => value?.ToLowerInvariant() switch
    {
        null => null,
        "bearer" or "oauth" or "token" => AgentAuth.Bearer,
        "api_key" or "key" or "header" => AgentAuth.KeyHeader,
        _ => throw new AgentException(
            $"agents config: '{name}' has auth '{value}'; expected \"bearer\" (OAuth tokens, SSO " +
            "gateways) or \"api_key\" (the provider's own key header)"),
    };

    /// <summary>
    /// Extra request headers. This is how a provider's OAuth mode is completed —
    /// Anthropic's needs <c>anthropic-beta: oauth-2025-04-20</c> alongside the
    /// bearer token, and it is a header rather than a flag because gateways each
    /// want their own.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ParseHeaders(string name, JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonObject obj)
            throw new AgentException($"agents config: '{name}'.headers must be an object of header -> value");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (header, value) in obj)
        {
            var v = Str(value) ?? throw new AgentException(
                $"agents config: '{name}'.headers['{header}'] must be a non-empty string");
            // A credential belongs in an env var or a token_command, not in a file
            // the deploy publishes — the same rule the inline api_key check states.
            if (header.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                throw new AgentException(
                    $"agents config: '{name}'.headers may not set Authorization — that is what " +
                    "auth/api_key_env/token_command are for.");
            headers[header] = v;
        }

        return headers.Count == 0 ? null : headers;
    }

    // A wrong type in the config is reported at its JSON path rather than
    // coerced: a temperature written as "0.2" and silently dropped is a
    // configuration that reads as applied and is not.
    private static string? Str(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s))
            throw new AgentException($"agents config: '{node.GetPath()}' must be a string");
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    private static double? Num(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonValue v || !v.TryGetValue<double>(out var d))
            throw new AgentException($"agents config: '{node.GetPath()}' must be a number");
        return d;
    }
}
