using System.Text.Json;
using Laplace.Chess.Service;

namespace Laplace.Endpoints.OpenAICompat;

internal interface ILichessStatusClient
{
    Task<LichessConnectivityStatus> StatusAsync(CancellationToken ct);
    Task<IReadOnlyList<LichessChatLine>> ChatAsync(string gameId, CancellationToken ct);
}

internal sealed class LichessStatusClient(HttpClient client) : ILichessStatusClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<LichessConnectivityStatus> StatusAsync(CancellationToken ct)
    {
        try
        {
            return await client.GetFromJsonAsync<LichessConnectivityStatus>("/status", Json, ct)
                ?? Offline("empty status response");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Offline($"managed service unavailable ({ex.GetType().Name})");
        }
    }

    public async Task<IReadOnlyList<LichessChatLine>> ChatAsync(string gameId, CancellationToken ct)
    {
        if (gameId.Length > 32 || !gameId.All(char.IsAsciiLetterOrDigit)) return [];
        try
        {
            return await client.GetFromJsonAsync<LichessChatLine[]>($"/games/{gameId}/chat", Json, ct) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return []; }
    }

    private static LichessConnectivityStatus Offline(string error) =>
        new(false, null, false, null, 0, 0, false, 0, [], error);
}
