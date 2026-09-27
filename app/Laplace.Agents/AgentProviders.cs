namespace Laplace.Agents;

/// <summary>
/// The request/response body shapes an external model host speaks. Many providers
/// share <see cref="OpenAiChat"/>, so a provider is a table row, not a client.
/// </summary>
public enum AgentWire
{
    /// <summary>POST {base}/chat/completions — OpenAI and every clone of it.</summary>
    OpenAiChat,

    /// <summary>POST {base}/responses — OpenAI Codex and other Responses-only models.</summary>
    OpenAiResponses,

    /// <summary>POST {base}/messages — Anthropic's Messages API.</summary>
    AnthropicMessages,

    /// <summary>POST {base}/models/{model}:generateContent — Google Generative Language.</summary>
    GoogleGenerative,
}

/// <summary>
/// How a credential is presented, independent of <see cref="AgentWire"/>: the same
/// body shape can take a key on the provider's header or a bearer token.
/// </summary>
public enum AgentAuth
{
    /// <summary>Authorization: Bearer {credential} — OpenAI and clones, and every OAuth token.</summary>
    Bearer,

    /// <summary>The credential on the provider's own header — x-api-key, x-goog-api-key.</summary>
    KeyHeader,
}

/// <summary>
/// One external model host: where to POST, how to authenticate, and which
/// environment variables carry its key. <see cref="DefaultModel"/> is null unless a
/// model id is declared in the table; otherwise the caller or <c>agents.json</c>
/// must name the model.
/// </summary>
public sealed record AgentProvider(
    string Id,
    AgentWire Wire,
    string DefaultBaseUrl,
    IReadOnlyList<string> ApiKeyEnvNames,
    bool RequiresKey = true,
    string? DefaultModel = null,
    string MaxTokensField = "max_tokens",
    AgentAuth Auth = AgentAuth.Bearer,
    string KeyHeader = "Authorization")
{
    /// <summary>
    /// Base URLs end at the version segment; the wire supplies the path suffix.
    /// </summary>
    public string ResolveBaseUrl(string? overrideUrl) =>
        (string.IsNullOrWhiteSpace(overrideUrl) ? DefaultBaseUrl : overrideUrl!).TrimEnd('/');
}

/// <summary>The installed provider table, plus the name-to-provider inference used for bare model ids.</summary>
public static class AgentProviders
{
    public const string SecretFile = "agents.env";

    private static readonly AgentProvider[] Table =
    [
        new("openai", AgentWire.OpenAiChat, "https://api.openai.com/v1",
            ["OPENAI_API_KEY"], MaxTokensField: "max_completion_tokens"),
        new("openai-responses", AgentWire.OpenAiResponses, "https://api.openai.com/v1",
            ["OPENAI_API_KEY"], MaxTokensField: "max_output_tokens"),
        // Key on x-api-key; an agent with auth "bearer" and a token_command sends an
        // OAuth token on Authorization instead (see AgentCatalog).
        new("anthropic", AgentWire.AnthropicMessages, "https://api.anthropic.com/v1",
            ["ANTHROPIC_API_KEY"], DefaultModel: "claude-opus-5",
            Auth: AgentAuth.KeyHeader, KeyHeader: "x-api-key"),
        new("xai", AgentWire.OpenAiChat, "https://api.x.ai/v1",
            ["XAI_API_KEY"]),
        // Key header on the Generative Language API. An OAuth-fronted host is an
        // agent with its own base_url, bearer auth and token_command.
        new("google", AgentWire.GoogleGenerative, "https://generativelanguage.googleapis.com/v1beta",
            ["GEMINI_API_KEY", "GOOGLE_API_KEY"],
            Auth: AgentAuth.KeyHeader, KeyHeader: "x-goog-api-key"),
        new("openrouter", AgentWire.OpenAiChat, "https://openrouter.ai/api/v1",
            ["OPENROUTER_API_KEY"]),
        new("groq", AgentWire.OpenAiChat, "https://api.groq.com/openai/v1",
            ["GROQ_API_KEY"]),
        new("deepseek", AgentWire.OpenAiChat, "https://api.deepseek.com/v1",
            ["DEEPSEEK_API_KEY"]),
        new("mistral", AgentWire.OpenAiChat, "https://api.mistral.ai/v1",
            ["MISTRAL_API_KEY"]),
        // Local and self-hosted routes: a missing key is not an error.
        new("ollama", AgentWire.OpenAiChat, "http://127.0.0.1:11434/v1",
            ["OLLAMA_API_KEY"], RequiresKey: false),
        new("openai-compatible", AgentWire.OpenAiChat, "",
            ["LAPLACE_AGENT_API_KEY"], RequiresKey: false),
        // This install's OpenAI-compatible interface; base URL is resolved from
        // LaplaceInstall.EndpointBaseUrl by AgentCatalog.
        new("laplace", AgentWire.OpenAiChat, "",
            ["LAPLACE_API_KEY"], RequiresKey: false),
    ];

    /// <summary>
    /// Vendor-branded prefixes only. Names served by many hosts are not inferred and
    /// must arrive as <c>provider/model</c> or through an alias.
    /// </summary>
    private static readonly (string Prefix, string Provider)[] NamePrefixes =
    [
        ("claude", "anthropic"),
        ("gpt", "openai"),
        ("chatgpt", "openai"),
        ("o1", "openai"),
        ("o3", "openai"),
        ("o4", "openai"),
        ("grok", "xai"),
        ("gemini", "google"),
        ("deepseek", "deepseek"),
        ("mistral", "mistral"),
        ("magistral", "mistral"),
        ("codestral", "mistral"),
    ];

    public static IReadOnlyList<AgentProvider> All => Table;

    public static bool TryGet(string? id, out AgentProvider provider)
    {
        provider = Table.FirstOrDefault(p =>
            string.Equals(p.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))!;
        return provider is not null;
    }

    public static AgentProvider Get(string id) =>
        TryGet(id, out var p)
            ? p
            : throw new AgentException(
                $"unknown provider '{id}'. Installed: {string.Join(", ", Table.Select(t => t.Id))}");

    /// <summary>The provider a bare model id belongs to, or null when the name is ambiguous.</summary>
    public static AgentProvider? InferFromModelName(string model)
    {
        var m = model.Trim().ToLowerInvariant();
        foreach (var (prefix, provider) in NamePrefixes)
            if (m.StartsWith(prefix, StringComparison.Ordinal))
                return Get(provider);
        return null;
    }
}

/// <summary>
/// A configuration or transport fault calling an external model host. A distinct
/// type so callers can separate it from argument errors without parsing text.
/// </summary>
public sealed class AgentException(string message, Exception? inner = null)
    : Exception(message, inner);
