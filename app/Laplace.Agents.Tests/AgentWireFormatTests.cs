using System.Text.Json.Nodes;
using Laplace.Agents;
using Xunit;

namespace Laplace.Agents.Tests;

/// <summary>
/// Body shaping and response reading per wire. These are the failures that return
/// HTTP 200 and an empty string: a field named for the wrong vendor, a content
/// block read at the wrong path, a refusal mistaken for silence.
/// </summary>
public sealed class AgentWireFormatTests
{
    private static AgentTarget Target(
        string providerId, string model = "m", int? maxTokens = null,
        double? temperature = null, string? system = null)
    {
        var provider = AgentProviders.Get(providerId);
        return new AgentTarget(
            providerId, provider, model, provider.ResolveBaseUrl(null),
            "key", maxTokens, temperature, system, provider.Auth);
    }

    // ---- URIs -------------------------------------------------------------

    [Fact]
    public void Uri_per_wire()
    {
        Assert.Equal("https://api.openai.com/v1/chat/completions",
            AgentWireFormat.BuildUri(Target("openai")).ToString());
        Assert.Equal("https://api.anthropic.com/v1/messages",
            AgentWireFormat.BuildUri(Target("anthropic")).ToString());
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-3-pro:generateContent",
            AgentWireFormat.BuildUri(Target("google", "gemini-3-pro")).ToString());
    }

    // ---- request bodies ---------------------------------------------------

    /// <summary>
    /// OpenAI rejects max_tokens on its reasoning models and every clone rejects
    /// max_completion_tokens, so the field name is per provider, not per wire.
    /// </summary>
    [Fact]
    public void Openai_caps_output_with_max_completion_tokens_and_clones_with_max_tokens()
    {
        var openai = AgentWireFormat.BuildBody(Target("openai", maxTokens: 64), new AgentRequest("hi"));
        Assert.Equal(64, (int)openai["max_completion_tokens"]!);
        Assert.Null(openai["max_tokens"]);

        var xai = AgentWireFormat.BuildBody(Target("xai", maxTokens: 64), new AgentRequest("hi"));
        Assert.Equal(64, (int)xai["max_tokens"]!);
        Assert.Null(xai["max_completion_tokens"]);
    }

    [Fact]
    public void Openai_body_puts_the_system_turn_first()
    {
        var body = AgentWireFormat.BuildBody(Target("openai"), new AgentRequest("q", System: "s"));
        var messages = (JsonArray)body["messages"]!;

        Assert.Equal(2, messages.Count);
        Assert.Equal("system", (string)messages[0]!["role"]!);
        Assert.Equal("s", (string)messages[0]!["content"]!);
        Assert.Equal("user", (string)messages[1]!["role"]!);
        Assert.Equal("q", (string)messages[1]!["content"]!);
    }

    [Fact]
    public void Request_system_outranks_the_alias_system()
    {
        var body = AgentWireFormat.BuildBody(
            Target("openai", system: "from-alias"), new AgentRequest("q", System: "from-call"));
        Assert.Equal("from-call", (string)((JsonArray)body["messages"]!)[0]!["content"]!);
    }

    [Fact]
    public void Alias_system_applies_when_the_call_omits_one()
    {
        var body = AgentWireFormat.BuildBody(Target("openai", system: "from-alias"), new AgentRequest("q"));
        Assert.Equal("from-alias", (string)((JsonArray)body["messages"]!)[0]!["content"]!);
    }

    /// <summary>
    /// Claude Opus 4.7 and later return 400 for temperature/top_p/top_k. An
    /// unrequested default would break every current Anthropic model on this lane.
    /// </summary>
    [Fact]
    public void Anthropic_body_omits_sampling_parameters_unless_asked_for()
    {
        var body = AgentWireFormat.BuildBody(Target("anthropic"), new AgentRequest("q"));
        Assert.Null(body["temperature"]);
        Assert.Null(body["top_p"]);

        var explicitly = AgentWireFormat.BuildBody(Target("anthropic"), new AgentRequest("q", Temperature: 0.4));
        Assert.Equal(0.4, (double)explicitly["temperature"]!);
    }

    /// <summary>
    /// max_tokens is required by the Messages API and bounds thinking as well as
    /// text; Claude Opus 5 thinks by default, so a small cap truncates the answer.
    /// </summary>
    [Fact]
    public void Anthropic_body_always_carries_a_max_tokens_with_headroom()
    {
        var body = AgentWireFormat.BuildBody(Target("anthropic"), new AgentRequest("q"));
        Assert.Equal(AgentWireFormat.AnthropicDefaultMaxTokens, (int)body["max_tokens"]!);

        var capped = AgentWireFormat.BuildBody(Target("anthropic", maxTokens: 512), new AgentRequest("q"));
        Assert.Equal(512, (int)capped["max_tokens"]!);
    }

    [Fact]
    public void Anthropic_body_carries_system_at_the_top_level_not_as_a_message()
    {
        var body = AgentWireFormat.BuildBody(Target("anthropic"), new AgentRequest("q", System: "s"));
        Assert.Equal("s", (string)body["system"]!);
        Assert.Single((JsonArray)body["messages"]!);
    }

    [Fact]
    public void Google_body_nests_the_prompt_in_contents_and_the_caps_in_generation_config()
    {
        var body = AgentWireFormat.BuildBody(
            Target("google", "gemini-3-pro", maxTokens: 100), new AgentRequest("q", System: "s"));

        Assert.Equal("q", (string)((JsonArray)((JsonArray)body["contents"]!)[0]!["parts"]!)[0]!["text"]!);
        Assert.Equal("s", (string)((JsonArray)body["systemInstruction"]!["parts"]!)[0]!["text"]!);
        Assert.Equal(100, (int)body["generationConfig"]!["maxOutputTokens"]!);
    }

    [Fact]
    public void Google_body_omits_generation_config_when_nothing_constrains_it()
    {
        var body = AgentWireFormat.BuildBody(Target("google", "gemini-3-pro"), new AgentRequest("q"));
        Assert.Null(body["generationConfig"]);
    }

    // ---- auth -------------------------------------------------------------

    [Fact]
    public void Auth_headers_are_per_wire()
    {
        using var openai = new HttpRequestMessage();
        AgentWireFormat.ApplyAuth(openai, Target("openai"));
        Assert.Equal("Bearer key", openai.Headers.GetValues("Authorization").Single());

        using var anthropic = new HttpRequestMessage();
        AgentWireFormat.ApplyAuth(anthropic, Target("anthropic"));
        Assert.Equal("key", anthropic.Headers.GetValues("x-api-key").Single());
        Assert.Equal(AgentWireFormat.AnthropicVersion,
            anthropic.Headers.GetValues("anthropic-version").Single());
        Assert.False(anthropic.Headers.Contains("Authorization"));

        using var google = new HttpRequestMessage();
        AgentWireFormat.ApplyAuth(google, Target("google"));
        Assert.Equal("key", google.Headers.GetValues("x-goog-api-key").Single());
    }

    [Fact]
    public void Keyless_provider_sends_no_authorization_header()
    {
        var provider = AgentProviders.Get("ollama");
        var target = new AgentTarget("ollama", provider, "llama3.2", provider.DefaultBaseUrl,
            ApiKey: null, null, null, null, provider.Auth);

        using var message = new HttpRequestMessage();
        AgentWireFormat.ApplyAuth(message, target);
        Assert.False(message.Headers.Contains("Authorization"));
    }

    // ---- responses --------------------------------------------------------

    [Fact]
    public void Openai_reply_reads_text_finish_reason_and_usage()
    {
        var (text, finish, input, output, note) = AgentWireFormat.ParseResponse(Target("openai"),
            JsonNode.Parse("""
            { "choices": [ { "finish_reason": "stop", "message": { "content": "hello" } } ],
              "usage": { "prompt_tokens": 11, "completion_tokens": 3 } }
            """));

        Assert.Equal("hello", text);
        Assert.Equal("stop", finish);
        Assert.Equal(11, input);
        Assert.Equal(3, output);
        Assert.Null(note);
    }

    /// <summary>Some OpenAI-compatible hosts return content as a part array, not a string.</summary>
    [Fact]
    public void Openai_reply_reads_content_part_arrays()
    {
        var (text, _, _, _, _) = AgentWireFormat.ParseResponse(Target("xai"),
            JsonNode.Parse("""
            { "choices": [ { "message": { "content": [
                { "type": "text", "text": "a" }, { "type": "text", "text": "b" } ] } } ] }
            """));

        Assert.Equal("ab", text);
    }

    [Fact]
    public void Openai_empty_reply_at_the_token_cap_says_so()
    {
        var (text, finish, _, _, note) = AgentWireFormat.ParseResponse(Target("openai"),
            JsonNode.Parse("""
            { "choices": [ { "finish_reason": "length", "message": { "content": null } } ] }
            """));

        Assert.Equal("", text);
        Assert.Equal("length", finish);
        Assert.Contains("max_tokens", note!);
    }

    [Fact]
    public void Anthropic_reply_concatenates_every_text_block_and_ignores_the_rest()
    {
        var (text, finish, input, output, _) = AgentWireFormat.ParseResponse(Target("anthropic"),
            JsonNode.Parse("""
            { "stop_reason": "end_turn",
              "content": [ { "type": "thinking", "thinking": "" },
                           { "type": "text", "text": "one " },
                           { "type": "text", "text": "two" } ],
              "usage": { "input_tokens": 5, "output_tokens": 2 } }
            """));

        Assert.Equal("one two", text);
        Assert.Equal("end_turn", finish);
        Assert.Equal(5, input);
        Assert.Equal(2, output);
    }

    /// <summary>
    /// A declined request is HTTP 200 with an empty content array. Reading
    /// content[0] before stop_reason is the documented way to break on this.
    /// </summary>
    [Fact]
    public void Anthropic_refusal_is_an_outcome_with_a_reason_not_an_exception()
    {
        var (text, finish, _, _, note) = AgentWireFormat.ParseResponse(Target("anthropic"),
            JsonNode.Parse("""
            { "stop_reason": "refusal", "content": [], "stop_details": { "category": "cyber" } }
            """));

        Assert.Equal("", text);
        Assert.Equal("refusal", finish);
        Assert.Contains("cyber", note!);
    }

    [Fact]
    public void Google_reply_concatenates_parts()
    {
        var (text, finish, input, output, _) = AgentWireFormat.ParseResponse(Target("google"),
            JsonNode.Parse("""
            { "candidates": [ { "finishReason": "STOP",
                "content": { "parts": [ { "text": "x" }, { "text": "y" } ] } } ],
              "usageMetadata": { "promptTokenCount": 7, "candidatesTokenCount": 2 } }
            """));

        Assert.Equal("xy", text);
        Assert.Equal("STOP", finish);
        Assert.Equal(7, input);
        Assert.Equal(2, output);
    }

    [Fact]
    public void Google_prompt_block_reports_the_reason()
    {
        var (text, finish, _, _, note) = AgentWireFormat.ParseResponse(Target("google"),
            JsonNode.Parse("""{ "promptFeedback": { "blockReason": "SAFETY" } }"""));

        Assert.Equal("", text);
        Assert.Equal("blocked", finish);
        Assert.Contains("SAFETY", note!);
    }

    [Fact]
    public void Vendor_error_body_becomes_an_agent_exception_carrying_its_message()
    {
        var ex = Assert.Throws<AgentException>(() => AgentWireFormat.ParseResponse(Target("openai"),
            JsonNode.Parse("""{ "error": { "type": "invalid_request_error", "message": "no such model" } }""")));

        Assert.Contains("no such model", ex.Message);
        Assert.Contains("invalid_request_error", ex.Message);
    }

    [Fact]
    public void Structurally_impossible_body_is_reported_rather_than_returned_as_empty_text()
    {
        Assert.Throws<AgentException>(() =>
            AgentWireFormat.ParseResponse(Target("openai"), JsonNode.Parse("""{ "choices": [] }""")));
    }
}
