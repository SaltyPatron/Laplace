using System.Text.Json.Nodes;

namespace Laplace.Agents;

/// <summary>One turn to put to an external agent.</summary>
public sealed record AgentRequest(
    string Prompt,
    string? System = null,
    int? MaxTokens = null,
    double? Temperature = null);

/// <summary>
/// What came back, with the provenance a caller needs to attribute it: which
/// route ran, which model answered, why it stopped, and what it cost.
/// </summary>
public sealed record AgentReply(
    string Agent,
    string Provider,
    string Model,
    string Text,
    string? FinishReason,
    long? InputTokens,
    long? OutputTokens,
    double LatencyMs,
    int Attempts,
    string? Note);

/// <summary>
/// Request shaping and response reading for the three wires. Pure and static so
/// the shapes are testable without a socket — every past LLM-client bug in this
/// class of code is a body field or a response path, not the transport.
/// </summary>
public static class AgentWireFormat
{
    /// <summary>
    /// Anthropic requires max_tokens and counts thinking against it; Claude Opus 5
    /// thinks by default, so a small cap truncates the answer rather than the
    /// reasoning. 16000 is the documented non-streaming default.
    /// </summary>
    public const int AnthropicDefaultMaxTokens = 16000;

    public const string AnthropicVersion = "2023-06-01";

    public static Uri BuildUri(AgentTarget target) => target.Provider.Wire switch
    {
        AgentWire.OpenAiChat => new Uri($"{target.BaseUrl}/chat/completions"),
        AgentWire.AnthropicMessages => new Uri($"{target.BaseUrl}/messages"),
        AgentWire.GoogleGenerative => new Uri(
            $"{target.BaseUrl}/models/{Uri.EscapeDataString(target.Model)}:generateContent"),
        _ => throw new AgentException($"unhandled wire {target.Provider.Wire}"),
    };

    public static JsonObject BuildBody(AgentTarget target, AgentRequest request)
    {
        var system = string.IsNullOrWhiteSpace(request.System) ? target.System : request.System;
        var maxTokens = request.MaxTokens ?? target.MaxTokens;
        var temperature = request.Temperature ?? target.Temperature;

        switch (target.Provider.Wire)
        {
            case AgentWire.OpenAiChat:
            {
                var messages = new JsonArray();
                if (!string.IsNullOrWhiteSpace(system))
                    messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.Prompt });

                var body = new JsonObject { ["model"] = target.Model, ["messages"] = messages };
                // OpenAI renamed the cap to max_completion_tokens and rejects the old
                // name on its reasoning models; every clone kept max_tokens. The field
                // name is provider data, not a branch here.
                if (maxTokens is { } mt) body[target.Provider.MaxTokensField] = mt;
                if (temperature is { } t) body["temperature"] = t;
                return body;
            }

            case AgentWire.AnthropicMessages:
            {
                var body = new JsonObject
                {
                    ["model"] = target.Model,
                    ["max_tokens"] = maxTokens ?? AnthropicDefaultMaxTokens,
                    ["messages"] = new JsonArray(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = request.Prompt,
                    }),
                };
                if (!string.IsNullOrWhiteSpace(system)) body["system"] = system;
                // Sampling parameters are REJECTED (400) on Claude Opus 4.7 and later,
                // so nothing is sent unless a caller or an alias asked for it by name.
                // An unrequested default here would break every current Anthropic model.
                if (temperature is { } t) body["temperature"] = t;
                return body;
            }

            case AgentWire.GoogleGenerative:
            {
                var body = new JsonObject
                {
                    ["contents"] = new JsonArray(new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray(new JsonObject { ["text"] = request.Prompt }),
                    }),
                };
                if (!string.IsNullOrWhiteSpace(system))
                    body["systemInstruction"] = new JsonObject
                    {
                        ["parts"] = new JsonArray(new JsonObject { ["text"] = system }),
                    };

                var gen = new JsonObject();
                if (maxTokens is { } mt) gen["maxOutputTokens"] = mt;
                if (temperature is { } t) gen["temperature"] = t;
                if (gen.Count > 0) body["generationConfig"] = gen;
                return body;
            }

            default:
                throw new AgentException($"unhandled wire {target.Provider.Wire}");
        }
    }

    /// <summary>
    /// Credential placement is the target's <see cref="AgentAuth"/>, not the wire's:
    /// Anthropic takes a key on <c>x-api-key</c> and an OAuth token on
    /// <c>Authorization: Bearer</c>, on the identical request body. Protocol headers
    /// that are not credentials (anthropic-version) ride the wire regardless.
    /// </summary>
    public static void ApplyAuth(HttpRequestMessage message, AgentTarget target)
    {
        if (target.ApiKey is not null)
        {
            var (header, value) = target.Auth == AgentAuth.Bearer
                ? ("Authorization", $"Bearer {target.ApiKey}")
                : (target.Provider.KeyHeader, target.ApiKey);
            message.Headers.TryAddWithoutValidation(header, value);
        }

        if (target.Provider.Wire == AgentWire.AnthropicMessages)
            message.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

        // Applied last so an operator can correct anything above — an SSO gateway
        // that wants its own version pin is a configuration, not a code change.
        if (target.Headers is null) return;
        foreach (var (header, value) in target.Headers)
        {
            message.Headers.Remove(header);
            message.Headers.TryAddWithoutValidation(header, value);
        }
    }

    /// <summary>
    /// Read a 2xx body. Text is returned EMPTY rather than as an exception when the
    /// model declined or was cut off — a refusal and a truncation are outcomes with
    /// a stop reason attached, and collapsing them into a thrown error loses the
    /// reason the caller needs to decide what to do next.
    /// </summary>
    public static (string Text, string? FinishReason, long? InputTokens, long? OutputTokens, string? Note)
        ParseResponse(AgentTarget target, JsonNode? root)
    {
        if (root is null)
            throw new AgentException($"{target.Provider.Id} returned an empty body");

        if (ExtractError(root) is { } err)
            throw new AgentException($"{target.Provider.Id} error: {err}");

        return target.Provider.Wire switch
        {
            AgentWire.OpenAiChat => ParseOpenAi(target, root),
            AgentWire.AnthropicMessages => ParseAnthropic(target, root),
            AgentWire.GoogleGenerative => ParseGoogle(target, root),
            _ => throw new AgentException($"unhandled wire {target.Provider.Wire}"),
        };
    }

    /// <summary>The vendor's own message out of an error body, for a 2xx-shaped error or a failed status.</summary>
    public static string? ExtractError(JsonNode? root)
    {
        if (root?["error"] is not { } error) return null;
        if (error is JsonValue) return error.ToString();
        var message = AsString(error["message"]);
        var type = AsString(error["type"]) ?? AsString(error["code"]);
        return (type, message) switch
        {
            (null, null) => error.ToJsonString(),
            (null, { } m) => m,
            ({ } t, null) => t,
            ({ } t, { } m) => $"[{t}] {m}",
        };
    }

    private static (string, string?, long?, long?, string?) ParseOpenAi(AgentTarget target, JsonNode root)
    {
        if (root["choices"] is not JsonArray choices || choices.Count == 0)
            throw new AgentException($"{target.Provider.Id} returned no choices: {Trim(root.ToJsonString())}");

        var choice = choices[0];
        var finish = AsString(choice?["finish_reason"]);
        var text = ReadContent(choice?["message"]?["content"]);

        var usage = root["usage"];
        var note = text.Length == 0
            ? finish switch
            {
                "length" => "empty reply: the model hit the token cap before emitting text — raise max_tokens",
                "content_filter" => "empty reply: the provider's content filter blocked the response",
                _ => "empty reply: the provider returned a choice with no text content",
            }
            : null;

        return (text, finish, AsLong(usage?["prompt_tokens"]), AsLong(usage?["completion_tokens"]), note);
    }

    private static (string, string?, long?, long?, string?) ParseAnthropic(AgentTarget target, JsonNode root)
    {
        var stop = AsString(root["stop_reason"]);
        var text = "";
        if (root["content"] is JsonArray blocks)
            text = string.Concat(blocks
                .Where(b => AsString(b?["type"]) == "text")
                .Select(b => AsString(b?["text"]) ?? ""));

        var usage = root["usage"];
        string? note = null;
        if (stop == "refusal")
        {
            // HTTP 200 with an empty content array. Code that reads content[0]
            // unconditionally breaks here, which is why stop_reason is read first.
            var category = AsString(root["stop_details"]?["category"]);
            note = "the model's safety classifiers declined this request" +
                   (category is null ? "" : $" (category: {category})");
        }
        else if (stop == "max_tokens" && text.Length == 0)
        {
            note = "empty reply: the token cap was consumed before any text was emitted — raise max_tokens";
        }

        return (text, stop, AsLong(usage?["input_tokens"]), AsLong(usage?["output_tokens"]), note);
    }

    private static (string, string?, long?, long?, string?) ParseGoogle(AgentTarget target, JsonNode root)
    {
        if (AsString(root["promptFeedback"]?["blockReason"]) is { } blocked)
            return ("", "blocked", null, null, $"the prompt was blocked upstream (reason: {blocked})");

        if (root["candidates"] is not JsonArray candidates || candidates.Count == 0)
            throw new AgentException($"{target.Provider.Id} returned no candidates: {Trim(root.ToJsonString())}");

        var candidate = candidates[0];
        var finish = AsString(candidate?["finishReason"]);
        var text = "";
        if (candidate?["content"]?["parts"] is JsonArray parts)
            text = string.Concat(parts.Select(p => AsString(p?["text"]) ?? ""));

        var usage = root["usageMetadata"];
        var note = text.Length == 0
            ? $"empty reply: the candidate carried no text (finishReason: {finish ?? "none"})"
            : null;

        return (text, finish, AsLong(usage?["promptTokenCount"]), AsLong(usage?["candidatesTokenCount"]), note);
    }

    /// <summary>
    /// OpenAI-compatible content is a string on most hosts and a content-part array
    /// on some (and null on a filtered or truncated turn). All three are read here
    /// so a working provider is not reported as an empty answer.
    /// </summary>
    private static string ReadContent(JsonNode? content) => content switch
    {
        null => "",
        JsonArray parts => string.Concat(parts.Select(p =>
            p is JsonValue v ? v.ToString() : AsString(p?["text"]) ?? "")),
        JsonValue value => value.TryGetValue<string>(out var s) ? s : value.ToString(),
        _ => content.ToJsonString(),
    };

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? AsLong(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    internal static string Trim(string s, int max = 600) =>
        s.Length <= max ? s : s[..max] + "…[truncated]";
}
