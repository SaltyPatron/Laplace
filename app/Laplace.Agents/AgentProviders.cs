namespace Laplace.Agents;

/// <summary>
/// The three request/response shapes every hosted chat model speaks today. A
/// provider is not a protocol: xAI, Groq, DeepSeek, Mistral, OpenRouter and
/// Ollama all speak <see cref="OpenAiChat"/>, so they cost one table row each
/// rather than one client each.
/// </summary>
public enum AgentWire
{
    /// <summary>POST {base}/chat/completions — OpenAI and every clone of it.</summary>
    OpenAiChat,

    /// <summary>POST {base}/messages — Anthropic's Messages API.</summary>
    AnthropicMessages,

    /// <summary>POST {base}/models/{model}:generateContent — Google Generative Language.</summary>
    GoogleGenerative,
}

/// <summary>
/// How a credential is presented. Separate from <see cref="AgentWire"/> because the
/// two are independent: Anthropic's Messages wire takes a key on <c>x-api-key</c>
/// but an OAuth access token on <c>Authorization: Bearer</c>, and an SSO gateway in
/// front of any provider takes a bearer token whatever the body shape is.
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
/// environment variables carry its key.
///
/// NO DEFAULT MODEL IS GUESSED. Only <c>anthropic</c> carries one, because it is
/// the only vendor whose current model id this repository can state from a
/// checked-in reference rather than from memory. Every other provider requires
/// the caller (or <c>agents.json</c>) to name the model — an invented id fails as
/// a 404 at the vendor, which reads as "the agent is down" rather than "nobody
/// said which model to use".
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
    /// Base URLs all end at the version segment, so the wire's path suffix is the
    /// only thing that varies. Getting this wrong is a 404 that looks like an
    /// outage, so the invariant is stated once here rather than per call site.
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
        // x-api-key by default. An OAuth profile token instead rides
        // Authorization: Bearer with the oauth beta header — set auth "bearer" and
        // a token_command on the agent; see AgentCatalog.
        new("anthropic", AgentWire.AnthropicMessages, "https://api.anthropic.com/v1",
            ["ANTHROPIC_API_KEY"], DefaultModel: "claude-opus-5",
            Auth: AgentAuth.KeyHeader, KeyHeader: "x-api-key"),
        new("xai", AgentWire.OpenAiChat, "https://api.x.ai/v1",
            ["XAI_API_KEY"]),
        // The Generative Language API takes a key header. Vertex AI is OAuth/ADC on
        // a different base URL — reach it as an openai-compatible or bearer agent
        // with a token_command, not by flipping a flag here.
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
        // Local and self-hosted runtimes: an unauthenticated loopback server is the
        // normal case, so a missing key is not an error for these three.
        new("ollama", AgentWire.OpenAiChat, "http://127.0.0.1:11434/v1",
            ["OLLAMA_API_KEY"], RequiresKey: false),
        new("openai-compatible", AgentWire.OpenAiChat, "",
            ["LAPLACE_AGENT_API_KEY"], RequiresKey: false),
        // Laplace's own OpenAI-compatible surface. Present so an agent can ask the
        // substrate the same way it asks a vendor, through one tool.
        new("laplace", AgentWire.OpenAiChat, "",
            ["LAPLACE_API_KEY"], RequiresKey: false),
    ];

    /// <summary>
    /// Vendor-branded prefixes only. A bare "llama" or "qwen" is served by a dozen
    /// hosts, so inferring one would silently route the call to the wrong bill;
    /// those names must arrive as <c>provider/model</c> or through an alias.
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
/// A configuration or transport fault on the external-agent lane. Distinct from
/// <see cref="ArgumentException"/> so a caller can tell "you asked wrongly" from
/// "the vendor is unreachable" without parsing message text.
/// </summary>
public sealed class AgentException(string message, Exception? inner = null)
    : Exception(message, inner);
