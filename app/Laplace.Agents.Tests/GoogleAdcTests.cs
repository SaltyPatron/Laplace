using System.Net;
using System.Text;
using Laplace.Agents;
using Xunit;

namespace Laplace.Agents.Tests;

/// <summary>
/// Vertex ADC: project and location resolution, the publisher URL, and the
/// refresh exchange. No test reads the machine's real credentials file.
/// </summary>
public sealed class GoogleAdcTests
{
    private sealed class ScriptedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<Uri?> Uris { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri);
            Bodies.Add(request.Content is null
                ? ""
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private static string HomeWith(string json, string? gcloudProject = null)
    {
        var home = Path.Combine(Path.GetTempPath(), "laplace-adc-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(home, ".config", "gcloud");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "application_default_credentials.json"), json);
        if (gcloudProject is not null)
        {
            File.WriteAllText(Path.Combine(dir, "active_config"), "default\n");
            var configs = Path.Combine(dir, "configurations");
            Directory.CreateDirectory(configs);
            File.WriteAllText(Path.Combine(configs, "config_default"), "[core]\nproject = " + gcloudProject + "\n");
        }

        return home;
    }

    private const string UserAdc = """
    {
      "type": "authorized_user",
      "client_id": "cid",
      "client_secret": "csec",
      "refresh_token": "super-refresh-secret",
      "quota_project_id": "quota-project"
    }
    """;

    private static Func<string, string?> Env(string home, params (string Key, string Value)[] extra) =>
        key =>
        {
            if (key == "HOME") return home;
            foreach (var (name, value) in extra)
                if (key == name) return value;
            return null;
        };

    [Fact]
    public void Global_location_has_no_location_prefix_and_a_regional_one_does()
    {
        Assert.Equal(
            "https://aiplatform.googleapis.com/v1/projects/project-x/locations/global/publishers/google",
            GoogleAdc.PublisherBaseUrl("project-x", "global"));
        Assert.Equal(
            "https://us-central1-aiplatform.googleapis.com/v1/projects/project-x/locations/us-central1/publishers/google",
            GoogleAdc.PublisherBaseUrl("project-x", "us-central1"));
    }

    [Fact]
    public void Publisher_url_rejects_a_project_that_could_rewrite_the_host()
    {
        var ex = Assert.Throws<AgentException>(() => GoogleAdc.PublisherBaseUrl("p/../../x", "global"));
        Assert.Contains("project", ex.Message);
    }

    [Fact]
    public void Mint_exchanges_the_refresh_token_and_bills_the_quota_project()
    {
        var home = HomeWith(UserAdc);
        var handler = new ScriptedHandler(HttpStatusCode.OK, """
            { "access_token": "ya29.test-token", "expires_in": 3600, "token_type": "Bearer" }
            """);
        try
        {
            var session = GoogleAdc.Mint(Env(home), handler);

            Assert.Equal("ya29.test-token", session.AccessToken);
            Assert.Equal("quota-project", session.ProjectId);
            Assert.Equal("global", session.Location);
            Assert.Equal(GoogleAdc.TokenEndpoint, handler.Uris.Single()?.ToString());
            var body = handler.Bodies.Single();
            Assert.Contains("grant_type=refresh_token", body, StringComparison.Ordinal);
            Assert.Contains("refresh_token=super-refresh-secret", body, StringComparison.Ordinal);
            Assert.Contains("client_id=cid", body, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Environment_project_and_location_outrank_the_credentials_file()
    {
        var home = HomeWith(UserAdc);
        try
        {
            var env = Env(home, ("GOOGLE_CLOUD_PROJECT", "from-env"), ("GOOGLE_CLOUD_LOCATION", "europe-west4"));
            Assert.True(GoogleAdc.TryPublisherBaseUrl(env, out var url));
            Assert.Equal(GoogleAdc.PublisherBaseUrl("from-env", "europe-west4"), url);
            Assert.True(GoogleAdc.TryQuotaProject(env, out var project));
            Assert.Equal("from-env", project);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Gcloud_config_project_is_used_when_the_credentials_file_has_none()
    {
        const string noQuota = """
        { "type": "authorized_user", "client_id": "cid", "client_secret": "csec", "refresh_token": "rtok" }
        """;
        var home = HomeWith(noQuota, gcloudProject: "from-gcloud");
        try
        {
            Assert.True(GoogleAdc.TryQuotaProject(Env(home), out var project));
            Assert.Equal("from-gcloud", project);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Missing_credentials_name_the_login_command_and_are_not_configured()
    {
        var home = Path.Combine(Path.GetTempPath(), "laplace-adc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var env = Env(home);
            Assert.False(GoogleAdc.IsConfigured(env));
            var ex = Assert.Throws<AgentException>(() => GoogleAdc.Mint(env));
            Assert.Contains("gcloud auth application-default login", ex.Message);
            Assert.Contains("set-quota-project", ex.Message);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Service_account_json_is_refused()
    {
        var home = HomeWith("""{ "type": "service_account", "client_email": "a@b.iam.gserviceaccount.com" }""");
        try
        {
            var ex = Assert.Throws<AgentException>(() => GoogleAdc.Mint(Env(home)));
            Assert.Contains("service_account", ex.Message);
            Assert.Contains("authorized_user", ex.Message);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void A_refused_refresh_does_not_echo_the_refresh_token()
    {
        var home = HomeWith(UserAdc);
        var handler = new ScriptedHandler(HttpStatusCode.BadRequest, """{ "error": "invalid_grant" }""");
        try
        {
            var ex = Assert.Throws<AgentException>(() => GoogleAdc.Mint(Env(home), handler));
            Assert.Contains("invalid_grant", ex.Message);
            Assert.DoesNotContain("super-refresh-secret", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Quota_header_is_set_once_and_an_explicit_header_wins()
    {
        var headers = GoogleAdc.WithQuotaProject(null, "quota-project");
        Assert.Equal("quota-project", headers["x-goog-user-project"]);

        var kept = GoogleAdc.WithQuotaProject(
            new Dictionary<string, string> { ["x-goog-user-project"] = "other" },
            "quota-project");
        Assert.Equal("other", kept["x-goog-user-project"]);
    }

    [Fact]
    public void Vertex_wire_posts_generateContent_on_the_publisher_url()
    {
        var provider = AgentProviders.Get("vertex");
        Assert.Equal(AgentWire.GoogleGenerative, provider.Wire);
        Assert.Equal(AgentAuth.Bearer, provider.Auth);
        Assert.True(provider.UsesAdc);

        var target = new AgentTarget(
            "vertex", provider, "gemini-3.8-flash",
            GoogleAdc.PublisherBaseUrl("project-x", "global"),
            "ya29.test-token", null, null, null, provider.Auth,
            GoogleAdc.WithQuotaProject(null, "project-x"));

        Assert.Equal(
            "https://aiplatform.googleapis.com/v1/projects/project-x/locations/global/publishers/google/models/gemini-3.8-flash:generateContent",
            AgentWireFormat.BuildUri(target).ToString());

        using var message = new HttpRequestMessage();
        AgentWireFormat.ApplyAuth(message, target);
        Assert.Equal("Bearer ya29.test-token", message.Headers.GetValues("Authorization").Single());
        Assert.Equal("project-x", message.Headers.GetValues("x-goog-user-project").Single());
        Assert.False(message.Headers.Contains("x-goog-api-key"));
    }

    [Fact]
    public void Resolving_vertex_without_adc_names_the_login_command()
    {
        var home = Path.Combine(Path.GetTempPath(), "laplace-adc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var catalog = AgentCatalog.Parse(null, null, Env(home));
            var row = catalog.Describe().Single(r => !r.IsAlias && r.Name == "vertex");
            Assert.False(row.Credentialed);
            Assert.Equal("adc", row.KeyEnv);
            Assert.Equal("", row.BaseUrl);

            var ex = Assert.Throws<AgentException>(() => catalog.Resolve("vertex/gemini-3.8-flash"));
            Assert.Contains("gcloud auth application-default login", ex.Message);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void A_token_command_outranks_adc_and_keeps_the_bearer()
    {
        const string cfg = """
        { "agents": { "v": {
            "provider": "vertex", "model": "gemini-3.8-flash",
            "base_url": "https://aiplatform.googleapis.com/v1/projects/p/locations/global/publishers/google",
            "token_command": "dotnet --version" } } }
        """;
        var home = Path.Combine(Path.GetTempPath(), "laplace-adc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var target = AgentCatalog.Parse(cfg, null, Env(home)).Resolve("v");
            Assert.Equal("vertex", target.Provider.Id);
            Assert.Equal(AgentAuth.Bearer, target.Auth);
            Assert.Equal("gemini-3.8-flash", target.Model);
            Assert.False(string.IsNullOrWhiteSpace(target.ApiKey));
            Assert.NotEqual("adc", target.ApiKey);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void A_bare_gemini_name_stays_on_the_api_key_provider()
    {
        var target = AgentCatalog.Parse(null, null, key => key == "GEMINI_API_KEY" ? "g" : null)
            .Resolve("gemini-3.8-flash");
        Assert.Equal("google", target.Provider.Id);
        Assert.Equal("g", target.ApiKey);
        Assert.StartsWith("https://generativelanguage.googleapis.com/", target.BaseUrl);
    }
}
