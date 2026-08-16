using Laplace.Agents;
using Xunit;

namespace Laplace.Agents.Tests;

/// <summary>
/// Routing and credential resolution. Every case here is a wrong answer this lane
/// can give without failing: the wrong vendor billed, a key read from the wrong
/// variable, or a secret written somewhere the deploy publishes.
/// </summary>
public sealed class AgentCatalogTests
{
    private const string Config = """
    {
      "default": "house",
      "agents": {
        "house":  { "provider": "anthropic", "model": "claude-opus-5", "max_tokens": 2048 },
        "cheap":  { "provider": "xai", "model": "grok-4-fast", "temperature": 0.2 },
        "local":  { "provider": "openai-compatible", "model": "llama-3.3-70b",
                    "base_url": "http://10.0.0.5:8000/v1", "api_key_env": "MY_LOCAL_KEY" }
      },
      "providers": {
        "openai": { "base_url": "https://gateway.internal/openai/v1" }
      }
    }
    """;

    private static AgentCatalog Catalog(params (string Key, string Value)[] env) =>
        AgentCatalog.Parse(Config, configPath: null, key =>
            env.FirstOrDefault(e => e.Key == key).Value);

    private static AgentCatalog Bare(params (string Key, string Value)[] env) =>
        AgentCatalog.Parse(null, configPath: null, key =>
            env.FirstOrDefault(e => e.Key == key).Value);

    [Fact]
    public void Alias_resolves_to_its_provider_model_and_knobs()
    {
        var target = Catalog(("ANTHROPIC_API_KEY", "sk-ant-x")).Resolve("house");

        Assert.Equal("house", target.Name);
        Assert.Equal("anthropic", target.Provider.Id);
        Assert.Equal("claude-opus-5", target.Model);
        Assert.Equal(2048, target.MaxTokens);
        Assert.Equal("sk-ant-x", target.ApiKey);
    }

    [Fact]
    public void Alias_lookup_is_case_insensitive()
    {
        var target = Catalog(("XAI_API_KEY", "k")).Resolve("CHEAP");
        Assert.Equal("xai", target.Provider.Id);
        Assert.Equal(0.2, target.Temperature);
    }

    [Fact]
    public void Qualified_reference_routes_to_the_named_provider()
    {
        var target = Bare(("XAI_API_KEY", "k")).Resolve("xai/grok-4");
        Assert.Equal("xai", target.Provider.Id);
        Assert.Equal("grok-4", target.Model);
        Assert.Equal("https://api.x.ai/v1", target.BaseUrl);
    }

    /// <summary>
    /// OpenRouter's own model ids are vendor/model. Splitting on every slash would
    /// route 'openrouter/anthropic/claude-x' to Anthropic directly — a different
    /// vendor, a different bill, and a key that is probably not set.
    /// </summary>
    [Fact]
    public void Qualified_reference_splits_once_so_nested_vendor_ids_survive()
    {
        var target = Bare(("OPENROUTER_API_KEY", "k")).Resolve("openrouter/anthropic/claude-opus-5");
        Assert.Equal("openrouter", target.Provider.Id);
        Assert.Equal("anthropic/claude-opus-5", target.Model);
    }

    [Theory]
    [InlineData("claude-opus-5", "anthropic")]
    [InlineData("gpt-5.1", "openai")]
    [InlineData("grok-4", "xai")]
    [InlineData("gemini-3-pro", "google")]
    public void Vendor_branded_names_infer_their_provider(string model, string provider)
    {
        var catalog = Bare(
            ("ANTHROPIC_API_KEY", "k"), ("OPENAI_API_KEY", "k"),
            ("XAI_API_KEY", "k"), ("GEMINI_API_KEY", "k"));

        Assert.Equal(provider, catalog.Resolve(model).Provider.Id);
    }

    /// <summary>
    /// A dozen hosts serve llama. Inferring one would silently bill the wrong
    /// vendor, so the ambiguity is an error that names the way out.
    /// </summary>
    [Fact]
    public void Ambiguous_bare_name_is_refused_with_the_qualified_form_in_the_message()
    {
        var ex = Assert.Throws<AgentException>(() => Bare().Resolve("llama-3.3-70b"));
        Assert.Contains("provider/model", ex.Message);
    }

    [Fact]
    public void Provider_argument_forces_the_route_and_bypasses_the_alias_table()
    {
        // 'house' is an Anthropic alias; forcing xai must call xai with 'house'
        // as a literal model id rather than quietly re-pointing the alias.
        var target = Catalog(("XAI_API_KEY", "k")).Resolve("house", providerId: "xai");
        Assert.Equal("xai", target.Provider.Id);
        Assert.Equal("house", target.Model);
    }

    [Fact]
    public void Missing_credential_names_the_variable_to_set()
    {
        var ex = Assert.Throws<AgentException>(() => Bare().Resolve("anthropic/claude-opus-5"));
        Assert.Contains("ANTHROPIC_API_KEY", ex.Message);
        Assert.Contains(AgentProviders.SecretFile, ex.Message);
    }

    [Fact]
    public void Alias_api_key_env_outranks_the_provider_default_variable()
    {
        var target = Catalog(("MY_LOCAL_KEY", "local-secret"), ("LAPLACE_AGENT_API_KEY", "wrong"))
            .Resolve("local");
        Assert.Equal("local-secret", target.ApiKey);
        Assert.Equal("http://10.0.0.5:8000/v1", target.BaseUrl);
    }

    [Fact]
    public void Loopback_providers_resolve_without_a_credential()
    {
        var target = Bare().Resolve("ollama/llama3.2");
        Assert.Null(target.ApiKey);
        Assert.Equal("http://127.0.0.1:11434/v1", target.BaseUrl);
    }

    [Fact]
    public void Provider_block_overrides_the_base_url()
    {
        var target = Catalog(("OPENAI_API_KEY", "k")).Resolve("openai/gpt-5.1");
        Assert.Equal("https://gateway.internal/openai/v1", target.BaseUrl);
    }

    [Fact]
    public void Google_accepts_either_of_its_two_conventional_key_variables()
    {
        Assert.Equal("g", Bare(("GOOGLE_API_KEY", "g")).Resolve("google/gemini-3-pro").ApiKey);
        Assert.Equal("g", Bare(("GEMINI_API_KEY", "g")).Resolve("google/gemini-3-pro").ApiKey);
    }

    [Fact]
    public void Env_default_outranks_the_config_default()
    {
        var catalog = Catalog(("XAI_API_KEY", "k"), ("LAPLACE_AGENT_DEFAULT", "cheap"));
        Assert.Equal("cheap", catalog.Resolve(null).Name);
    }

    [Fact]
    public void Config_default_applies_when_no_model_is_named()
    {
        Assert.Equal("house", Catalog(("ANTHROPIC_API_KEY", "k")).Resolve(null).Name);
    }

    [Fact]
    public void No_model_and_no_default_reports_what_would_make_the_call_work()
    {
        var ex = Assert.Throws<AgentException>(() => Bare().Resolve(null));
        Assert.Contains("LAPLACE_AGENT_DEFAULT", ex.Message);
    }

    /// <summary>
    /// The deploy syncs agents.json into /opt/laplace/app; laplace-api.env is
    /// excluded from that payload precisely because it holds secrets. A key pasted
    /// into the config would ride the publish, so the parse refuses it outright
    /// rather than working and being wrong later.
    /// </summary>
    [Fact]
    public void Inline_api_key_in_the_config_is_refused()
    {
        const string leaky = """
        { "agents": { "bad": { "provider": "openai", "model": "gpt-5.1", "api_key": "sk-live-abc" } } }
        """;

        var ex = Assert.Throws<AgentException>(() => AgentCatalog.Parse(leaky, null, _ => null));
        Assert.Contains("api_key_env", ex.Message);
    }

    [Fact]
    public void Unknown_provider_in_the_config_fails_at_parse_not_at_call_time()
    {
        const string bogus = """
        { "agents": { "x": { "provider": "notaprovider", "model": "m" } } }
        """;

        var ex = Assert.Throws<AgentException>(() => AgentCatalog.Parse(bogus, null, _ => null));
        Assert.Contains("notaprovider", ex.Message);
    }

    [Fact]
    public void Malformed_config_is_reported_as_a_config_fault()
    {
        var ex = Assert.Throws<AgentException>(() => AgentCatalog.Parse("{ not json", null, _ => null));
        Assert.Contains("valid JSON", ex.Message);
    }

    [Fact]
    public void Describe_reports_credential_state_by_variable_name_and_never_the_value()
    {
        var rows = Catalog(("ANTHROPIC_API_KEY", "sk-ant-supersecret")).Describe();

        var house = rows.Single(r => r.Name == "house");
        Assert.True(house.Credentialed);
        Assert.True(house.IsDefault);
        Assert.Equal("ANTHROPIC_API_KEY", house.KeyEnv);

        var openai = rows.Single(r => r is { Name: "openai", IsAlias: false });
        Assert.False(openai.Credentialed);

        var everything = string.Join("|", rows.Select(r =>
            $"{r.Name}|{r.Provider}|{r.Model}|{r.BaseUrl}|{r.KeyEnv}"));
        Assert.DoesNotContain("supersecret", everything, StringComparison.Ordinal);
    }

    /// <summary>
    /// The silent version of this loses every alias and reports the catalog as
    /// merely empty — which reads as "my agents disappeared", and then routes the
    /// call to a different model than the one that was configured.
    /// </summary>
    [Fact]
    public void Explicit_config_path_that_does_not_exist_is_an_error_not_a_fall_through()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"laplace-agents-{Guid.NewGuid():N}.json");

        var ex = Assert.Throws<AgentException>(() => AgentCatalog.DiscoverConfigPath(
            key => key == "LAPLACE_AGENTS_CONFIG" ? missing : null));

        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void Explicit_config_path_that_exists_wins_the_search()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-agents-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        try
        {
            Assert.Equal(path, AgentCatalog.DiscoverConfigPath(
                key => key == "LAPLACE_AGENTS_CONFIG" ? path : null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Describe_lists_every_installed_provider_alongside_the_aliases()
    {
        var rows = Bare().Describe();
        foreach (var provider in AgentProviders.All)
            Assert.Contains(rows, r => !r.IsAlias && r.Name == provider.Id);
    }
}
