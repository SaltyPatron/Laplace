using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>Exercise the real roster-to-profile adapter against committed game evidence.</summary>
[Trait("Tier", "live")]
public sealed class ChessProfileLiveTests
{
    [SkippableFact]
    public async Task WitnessedRosterPlayer_OpensProfileDespiteRenderedTypeSpelling()
    {
        await using var client = new SubstrateClient();
        var roster = await client.ChessPlayersAsync(
            1, 0, null, null, "games", "desc", CancellationToken.None);
        Skip.If(roster.Players.Count == 0, "standing substrate has no chess players");
        var player = Assert.Single(roster.Players);
        Assert.True(player.Games > 0, "live proof requires a witnessed playing");
        var profile = await client.ChessPlayerAsync(player.IdHex, 1, CancellationToken.None);
        Assert.NotNull(profile);
        Assert.Equal(player.IdHex, profile.IdHex);
        Assert.Equal(player.Name, profile.Name);
    }
}
