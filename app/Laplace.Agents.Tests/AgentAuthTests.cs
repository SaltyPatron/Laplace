using Laplace.Agents;
using Xunit;

namespace Laplace.Agents.Tests;

/// <summary>
/// Credential placement and minting. The failure these guard against is a
/// perfectly-shaped request rejected as unauthenticated because the token went on
/// the header the provider does not read.
/// </summary>
public sealed class AgentAuthTests
{
    private static AgentCatalog Catalog(string json, params (string Key, string Value)[] env) =>
        AgentCatalog.Parse(json, configPath: null, key =>
            env.FirstOrDefault(e => e.Key == key).Value);

    private static HttpRequestMessage Sign(AgentTarget target)
    {
        var message = new HttpRequestMessage();
        AgentWireFormat.ApplyAuth(message, target);
        return message;
    }

    // ---- placement ---------------------------------------------------------

    [Fact]
    public void Anthropic_defaults_to_its_own_key_header_not_authorization()
    {
        var target = Catalog("""{"agents":{}}""", ("ANTHROPIC_API_KEY", "sk-ant"))
            .Resolve("anthropic/claude-opus-5");

        Assert.Equal(AgentAuth.KeyHeader, target.Auth);
        using var signed = Sign(target);
        Assert.Equal("sk-ant", signed.Headers.GetValues("x-api-key").Single());
        Assert.False(signed.Headers.Contains("Authorization"));
    }

    /// <summary>
    /// An Anthropic OAuth profile token is rejected on x-api-key — it has to ride
    /// Authorization: Bearer, with the oauth beta header alongside it. Both are
    /// configuration here, so the OAuth path needs no code change.
    /// </summary>
    [Fact]
    public void Bearer_auth_moves_the_credential_and_keeps_the_protocol_header()
    {
        const string config = """
        { "agents": { "oauth": {
            "provider": "anthropic", "model": "claude-opus-5",
            "auth": "bearer", "api_key_env": "ANT_OAUTH_TOKEN",
            "headers": { "anthropic-beta": "oauth-2025-04-20" } } } }
        """;

        var target = Catalog(config, ("ANT_OAUTH_TOKEN", "oat01-abc")).Resolve("oauth");

        Assert.Equal(AgentAuth.Bearer, target.Auth);
        using var signed = Sign(target);
        Assert.Equal("Bearer oat01-abc", signed.Headers.GetValues("Authorization").Single());
        Assert.False(signed.Headers.Contains("x-api-key"));
        Assert.Equal("2023-06-01", signed.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("oauth-2025-04-20", signed.Headers.GetValues("anthropic-beta").Single());
    }

    [Fact]
    public void Api_key_auth_can_be_forced_onto_a_bearer_provider()
    {
        const string config = """
        { "agents": { "odd": { "provider": "openai", "model": "m", "auth": "api_key" } } }
        """;

        var target = Catalog(config, ("OPENAI_API_KEY", "k")).Resolve("odd");
        using var signed = Sign(target);
        Assert.Equal("k", signed.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public void Unknown_auth_mode_is_refused_at_parse_with_the_two_valid_spellings()
    {
        var ex = Assert.Throws<AgentException>(() => AgentCatalog.Parse(
            """{"agents":{"x":{"provider":"openai","model":"m","auth":"mtls"}}}""", null, _ => null));

        Assert.Contains("bearer", ex.Message);
        Assert.Contains("api_key", ex.Message);
    }

    [Fact]
    public void Headers_may_not_smuggle_a_credential_through_authorization()
    {
        var ex = Assert.Throws<AgentException>(() => AgentCatalog.Parse(
            """
            {"agents":{"x":{"provider":"openai","model":"m",
             "headers":{"Authorization":"Bearer sk-live-abc"}}}}
            """, null, _ => null));

        Assert.Contains("Authorization", ex.Message);
        Assert.Contains("token_command", ex.Message);
    }

    // ---- minting -----------------------------------------------------------

    /// <summary>
    /// `dotnet --version` stands in for `ant auth print-credentials --access-token`:
    /// a command on every box that runs these tests, exiting 0 with one line on
    /// stdout — the exact contract a token printer has to satisfy.
    /// </summary>
    [Fact]
    public void Token_command_output_becomes_the_credential()
    {
        const string config = """
        { "agents": { "minted": { "provider": "openai", "model": "m",
            "auth": "bearer", "token_command": "dotnet --version" } } }
        """;

        var target = Catalog(config).Resolve("minted");

        Assert.False(string.IsNullOrWhiteSpace(target.ApiKey));
        Assert.DoesNotContain('\n', target.ApiKey!);
        using var signed = Sign(target);
        Assert.Equal($"Bearer {target.ApiKey}", signed.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public void Token_command_outranks_a_stale_environment_variable()
    {
        const string config = """
        { "agents": { "minted": { "provider": "openai", "model": "m",
            "token_command": "dotnet --version" } } }
        """;

        var target = Catalog(config, ("OPENAI_API_KEY", "stale-static-key")).Resolve("minted");
        Assert.NotEqual("stale-static-key", target.ApiKey);
    }

    [Fact]
    public void A_command_that_prints_a_document_instead_of_a_token_is_refused()
    {
        const string config = """
        { "agents": { "chatty": { "provider": "openai", "model": "m",
            "token_command": "dotnet --info" } } }
        """;

        var ex = Assert.Throws<AgentException>(() => Catalog(config).Resolve("chatty"));
        Assert.Contains("one line", ex.Message);
    }

    [Fact]
    public void A_command_that_does_not_exist_names_itself_in_the_failure()
    {
        const string config = """
        { "agents": { "broken": { "provider": "openai", "model": "m",
            "token_command": "laplace-no-such-token-binary --access-token" } } }
        """;

        var ex = Assert.Throws<AgentException>(() => Catalog(config).Resolve("broken"));
        Assert.Contains("laplace-no-such-token-binary", ex.Message);
    }

    [Fact]
    public void A_command_that_fails_surfaces_its_exit_code()
    {
        const string config = """
        { "agents": { "failing": { "provider": "openai", "model": "m",
            "token_command": "dotnet --no-such-flag" } } }
        """;

        var ex = Assert.Throws<AgentException>(() => Catalog(config).Resolve("failing"));
        Assert.Contains("exited", ex.Message);
    }

    /// <summary>
    /// The routing table is polled by the operator UI. Minting a token per row per
    /// refresh would turn an inventory into a login storm, so Describe reports the
    /// source and runs nothing — proven here by a command that could only fail.
    /// </summary>
    [Fact]
    public void Describe_reports_a_token_command_without_executing_it()
    {
        const string config = """
        { "agents": { "minted": { "provider": "openai", "model": "m",
            "token_command": "laplace-no-such-token-binary" } } }
        """;

        var row = Catalog(config).Describe().Single(r => r.Name == "minted");

        Assert.True(row.Credentialed);
        Assert.Equal("token_command", row.KeyEnv);
        Assert.Contains("laplace-no-such-token-binary", row.CredentialSource);
    }

    [Fact]
    public void Describe_reports_the_auth_mode_each_route_would_use()
    {
        var rows = Catalog("""{"agents":{}}""").Describe();
        Assert.Equal("keyheader", rows.Single(r => r is { Name: "anthropic", IsAlias: false }).Auth);
        Assert.Equal("bearer", rows.Single(r => r is { Name: "openai", IsAlias: false }).Auth);
    }

    // ---- argv splitting ----------------------------------------------------

    [Theory]
    [InlineData("ant auth print-credentials --access-token",
        new[] { "ant", "auth", "print-credentials", "--access-token" })]
    [InlineData("\"/opt/my tools/mint\" --flag", new[] { "/opt/my tools/mint", "--flag" })]
    [InlineData("sh -c 'echo hi'", new[] { "sh", "-c", "echo hi" })]
    [InlineData("  spaced   out  ", new[] { "spaced", "out" })]
    public void Token_command_splits_on_whitespace_honouring_quotes(string command, string[] expected)
        => Assert.Equal(expected, TokenCommand.Split(command));

    [Fact]
    public void Unterminated_quote_in_a_token_command_is_an_error_not_a_silent_argument()
        => Assert.Throws<AgentException>(() => TokenCommand.Split("mint --flag \"unclosed"));
}
