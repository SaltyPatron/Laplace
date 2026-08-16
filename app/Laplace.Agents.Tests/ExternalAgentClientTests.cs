using System.Net;
using System.Text;
using Laplace.Agents;
using Xunit;

namespace Laplace.Agents.Tests;

/// <summary>
/// Transport behaviour against a scripted handler: no socket, no vendor, no
/// network flake. What is asserted is the part a caller cannot see from a reply —
/// what was sent, how many times, and what a failure says.
/// </summary>
public sealed class ExternalAgentClientTests
{
    private sealed class ScriptedHandler(params (HttpStatusCode Status, string Body)[] script)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var (status, body) = script[Math.Min(Requests.Count - 1, script.Length - 1)];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static AgentTarget Target(string providerId = "openai", string model = "gpt-test")
    {
        var provider = AgentProviders.Get(providerId);
        return new AgentTarget(providerId, provider, model, provider.ResolveBaseUrl(null),
            "sk-test", null, null, null, provider.Auth);
    }

    private static ExternalAgentClient Client(HttpMessageHandler handler) =>
        // Zero backoff: the retry policy is under test, not the wall clock.
        new(handler, retryBaseDelay: TimeSpan.Zero);

    private const string Ok = """
    { "choices": [ { "finish_reason": "stop", "message": { "content": "pong" } } ],
      "usage": { "prompt_tokens": 4, "completion_tokens": 1 } }
    """;

    [Fact]
    public async Task Successful_call_returns_the_reply_with_its_provenance()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, Ok));
        using var client = Client(handler);

        var reply = await client.AskAsync(Target(), new AgentRequest("ping"), TimeSpan.FromSeconds(5));

        Assert.Equal("pong", reply.Text);
        Assert.Equal("openai", reply.Provider);
        Assert.Equal("gpt-test", reply.Model);
        Assert.Equal(4, reply.InputTokens);
        Assert.Equal(1, reply.OutputTokens);
        Assert.Equal(1, reply.Attempts);
        Assert.Single(handler.Requests);

        var sent = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://api.openai.com/v1/chat/completions", sent.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", sent.Headers.GetValues("Authorization").Single());
        Assert.Contains("\"model\":\"gpt-test\"", handler.Bodies[0]);
        Assert.Contains("ping", handler.Bodies[0]);
    }

    [Fact]
    public async Task Throttling_is_retried_and_the_attempt_count_is_reported()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.TooManyRequests, """{"error":{"message":"slow down"}}"""),
            (HttpStatusCode.OK, Ok));
        using var client = Client(handler);

        var reply = await client.AskAsync(Target(), new AgentRequest("ping"), TimeSpan.FromSeconds(5));

        Assert.Equal("pong", reply.Text);
        Assert.Equal(2, reply.Attempts);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Transient_server_errors_are_retried_to_the_attempt_ceiling_then_surfaced()
    {
        var handler = new ScriptedHandler((HttpStatusCode.BadGateway, """{"error":{"message":"upstream"}}"""));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<AgentException>(() =>
            client.AskAsync(Target(), new AgentRequest("ping"), TimeSpan.FromSeconds(5)));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("502", ex.Message);
        Assert.Contains("upstream", ex.Message);
    }

    /// <summary>
    /// A bad key is not transient. Retrying it burns the caller's deadline and,
    /// on some providers, counts against a lockout.
    /// </summary>
    [Fact]
    public async Task Authentication_failure_is_not_retried_and_names_the_variable_to_fix()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.Unauthorized, """{"error":{"message":"invalid api key"}}"""));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<AgentException>(() =>
            client.AskAsync(Target(), new AgentRequest("ping"), TimeSpan.FromSeconds(5)));

        Assert.Single(handler.Requests);
        Assert.Contains("OPENAI_API_KEY", ex.Message);
    }

    /// <summary>
    /// Observed against the live vendor: OpenAI's 401 body quotes the rejected key
    /// verbatim. Forwarding that writes the credential into the caller's context and
    /// every log downstream of it, from a path that only fires when something is
    /// already wrong.
    /// </summary>
    [Fact]
    public async Task Vendor_error_text_never_carries_the_credential_back_to_the_caller()
    {
        var handler = new ScriptedHandler((HttpStatusCode.Unauthorized,
            """{"error":{"message":"Incorrect API key provided: sk-test. Find yours at ..."}}"""));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<AgentException>(() =>
            client.AskAsync(Target(), new AgentRequest("ping"), TimeSpan.FromSeconds(5)));

        Assert.DoesNotContain("sk-test", ex.Message, StringComparison.Ordinal);
        Assert.Contains("redacted", ex.Message, StringComparison.Ordinal);
        // The rest of the vendor's message still has to survive — it is the fix.
        Assert.Contains("Incorrect API key provided", ex.Message);
    }

    [Fact]
    public async Task Unknown_model_is_not_retried_and_says_which_model_and_host_refused()
    {
        var handler = new ScriptedHandler((HttpStatusCode.NotFound, """{"error":{"message":"no model"}}"""));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<AgentException>(() =>
            client.AskAsync(Target(model: "nope"), new AgentRequest("ping"), TimeSpan.FromSeconds(5)));

        Assert.Single(handler.Requests);
        Assert.Contains("nope", ex.Message);
        Assert.Contains("api.openai.com", ex.Message);
    }

    /// <summary>
    /// An HTML error page from a proxy in front of the vendor is the classic
    /// "worked in curl, threw a JsonException in production" case.
    /// </summary>
    [Fact]
    public async Task Non_json_success_body_is_reported_as_an_agent_fault()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, "<html>gateway</html>"));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<AgentException>(() =>
            client.AskAsync(Target(), new AgentRequest("ping"), TimeSpan.FromSeconds(5)));

        Assert.Contains("non-JSON", ex.Message);
    }

    [Fact]
    public async Task Anthropic_calls_carry_the_version_header_and_the_key_off_the_authorization_slot()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """
        { "stop_reason": "end_turn", "content": [ { "type": "text", "text": "hi" } ],
          "usage": { "input_tokens": 2, "output_tokens": 1 } }
        """));
        using var client = Client(handler);

        var reply = await client.AskAsync(
            Target("anthropic", "claude-opus-5"), new AgentRequest("ping"), TimeSpan.FromSeconds(5));

        Assert.Equal("hi", reply.Text);
        var sent = handler.Requests[0];
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.RequestUri!.ToString());
        Assert.Equal("sk-test", sent.Headers.GetValues("x-api-key").Single());
        Assert.Equal(AgentWireFormat.AnthropicVersion, sent.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public async Task Empty_prompt_is_refused_before_a_request_is_made()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, Ok));
        using var client = Client(handler);

        await Assert.ThrowsAsync<AgentException>(() =>
            client.AskAsync(Target(), new AgentRequest("   "), TimeSpan.FromSeconds(5)));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Deadline_expiry_reports_the_budget_that_was_exceeded()
    {
        var handler = new StallingHandler();
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<AgentException>(() =>
            client.AskAsync(Target(), new AgentRequest("ping"), TimeSpan.FromMilliseconds(50)));

        Assert.Contains("timeout_seconds", ex.Message);
    }

    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
