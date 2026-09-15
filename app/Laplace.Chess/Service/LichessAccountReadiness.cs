using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Laplace.Chess.Service;

/// <summary>Observed account prerequisites, separate from event-stream connectivity.</summary>
public sealed record LichessAccountReadiness(
    bool? TokenValid = null,
    bool? BotAccount = null,
    bool? BotPlayScope = null,
    string? Username = null,
    string? Error = null)
{
    public bool Ready => TokenValid == true && BotAccount == true && BotPlayScope == true
        && !string.IsNullOrWhiteSpace(Username) && Error is null;

    public static async Task<LichessAccountReadiness> CheckAsync(string token, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://lichess.org") };
        return await CheckAsync(http, token, ct).ConfigureAwait(false);
    }

    internal static async Task<LichessAccountReadiness> CheckAsync(
        HttpClient http, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new(TokenValid: false, Error: "No Lichess token; configure LICHESS_TOKEN or LICHESS_API with bot:play permission.");

        token = token.Trim();
        var result = new LichessAccountReadiness();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var accountRequest = new HttpRequestMessage(HttpMethod.Get, "/api/account");
            accountRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var accountResponse = await http.SendAsync(accountRequest, budget.Token).ConfigureAwait(false);
            if (!accountResponse.IsSuccessStatusCode)
                return HttpFailure(result, accountResponse.StatusCode, "account authentication");

            using var account = await ReadJsonAsync(accountResponse, budget.Token).ConfigureAwait(false);
            if (account.RootElement.ValueKind != JsonValueKind.Object
                || !account.RootElement.TryGetProperty("username", out var username)
                || username.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(username.GetString()))
                return result with { Error = "Lichess account response has no username; account access could not be verified." };

            result = result with
            {
                TokenValid = true,
                Username = username.GetString(),
                BotAccount = account.RootElement.TryGetProperty("title", out var title)
                    && title.ValueKind == JsonValueKind.String && title.GetString() == "BOT"
            };

            // Official scope probe: POST /api/token/test, not the OAuth exchange at /api/token.
            // The response's property name IS the secret token. Never log its body or parser errors.
            // https://github.com/lichess-org/api/blob/master/doc/specs/tags/oauth/api-token-test.yaml
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/api/token/test")
            {
                Content = new StringContent(token, Encoding.UTF8, "text/plain")
            };
            using var tokenResponse = await http.SendAsync(tokenRequest, budget.Token).ConfigureAwait(false);
            if (!tokenResponse.IsSuccessStatusCode)
                return HttpFailure(result, tokenResponse.StatusCode, "token scope verification");

            using var tokenInfo = await ReadJsonAsync(tokenResponse, budget.Token).ConfigureAwait(false);
            if (tokenInfo.RootElement.ValueKind != JsonValueKind.Object
                || !tokenInfo.RootElement.TryGetProperty(token, out var info))
                return result with { Error = "Lichess token response omitted the token result; bot:play permission could not be verified." };
            if (info.ValueKind == JsonValueKind.Null)
                return result with { TokenValid = false, Error = "Lichess token is invalid, expired, or revoked; configure a valid token with bot:play permission." };
            if (info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("scopes", out var scopes)
                || scopes.ValueKind != JsonValueKind.String)
                return result with { Error = "Lichess token response omitted scopes; bot:play permission could not be verified." };

            result = result with
            {
                BotPlayScope = (scopes.GetString() ?? "").Split(',', StringSplitOptions.TrimEntries)
                    .Contains("bot:play", StringComparer.Ordinal)
            };
            var missing = new List<string>();
            if (result.BotAccount != true)
                missing.Add($"@{result.Username} is not a BOT account; configure a BOT account (account upgrade is irreversible and is never performed automatically)");
            if (result.BotPlayScope != true)
                missing.Add("token lacks bot:play permission; create a token with Play games with the bot API enabled");
            return missing.Count == 0 ? result : result with { Error = "Lichess: " + string.Join("; ", missing) + "." };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return result with { Error = "Lichess account verification timed out; check HTTPS access to lichess.org and retry." };
        }
        catch (HttpRequestException)
        {
            return result with { Error = "Lichess account verification could not reach lichess.org; check DNS, HTTPS, and network access." };
        }
        catch (JsonException)
        {
            return result with { Error = "Lichess returned invalid JSON while verifying account access." };
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static LichessAccountReadiness HttpFailure(
        LichessAccountReadiness result, HttpStatusCode code, string operation) => code switch
    {
        HttpStatusCode.Unauthorized => result with
        {
            TokenValid = false,
            Error = "Lichess token was rejected (HTTP 401); replace an invalid, expired, or revoked token with one granting bot:play permission."
        },
        HttpStatusCode.Forbidden => result with
        {
            Error = $"Lichess denied {operation} (HTTP 403); verify the account and its token permissions."
        },
        HttpStatusCode.TooManyRequests => result with
        {
            Error = "Lichess rate limit reached (HTTP 429); wait at least one minute before retrying account verification."
        },
        _ => result with { Error = $"Lichess {operation} failed (HTTP {(int)code}); retry when the upstream API is available." }
    };
}
