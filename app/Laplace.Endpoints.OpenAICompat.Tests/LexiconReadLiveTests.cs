using System.Text.Json;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

[Trait("Category", "RequiresNetwork")]
[Trait("Tier", "live")]
public sealed class LexiconReadLiveTests
{
    [SkippableFact]
    public async Task RecordedMeaningsAndExamplesReachBothSidesWithWitnessedConnections()
    {
        string? connection = Environment.GetEnvironmentVariable("LAPLACE_LEXICON_ACCEPTANCE_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connection), "Explicit seeded lexicon acceptance connection not supplied");
        await using var client = new SubstrateClient(connection);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var field = await client.ResponseFieldAsync("love", "hate",512,0,cancellation.Token);
        Assert.NotNull(field);
        string? evidence = Environment.GetEnvironmentVariable("LAPLACE_LIVE_READ_EVIDENCE_FILE");
        if (!string.IsNullOrWhiteSpace(evidence))
            await File.WriteAllTextAsync(evidence,JsonSerializer.Serialize(field));
        Assert.NotNull(field.Readings);
        Assert.Contains(field.Readings,row => row.Side == 1);
        Assert.Contains(field.Readings,row => row.Side == 2);
        Assert.All(field.Readings,row =>
        {
            Assert.True(row.ClaimIds.Count >= 2);
            Assert.True(row.Witnesses > 0);
            Assert.False(string.IsNullOrWhiteSpace(row.Text));
        });
        Assert.Contains(field.Readings,row => row.Text.Contains("positive emotion",StringComparison.OrdinalIgnoreCase));
        Assert.Contains(field.Readings.Where(row => row.Side==1).Take(2),
            row => row.Text.Contains("positive emotion",StringComparison.OrdinalIgnoreCase));
        Assert.Contains(field.Readings,row => row.Text.Contains("dislike",StringComparison.OrdinalIgnoreCase));
        Assert.Contains(field.Readings,row => row.Relation.Contains("example",StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(field.Readings,row => row.Text.Contains("temporary red mark",StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(field.Connections);
        Assert.All(field.Connections,row => Assert.False(row.Relation.StartsWith('[')));
        Assert.Contains(field.Connections,row => row.Relation == "is antonym of" && row.Witnesses > 0);
    }
}
