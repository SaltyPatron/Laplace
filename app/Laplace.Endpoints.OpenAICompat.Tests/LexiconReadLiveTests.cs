using System.Text.Json;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

[Trait("Category", "RequiresNetwork")]
[Trait("Tier", "live")]
public sealed class LexiconReadLiveTests
{
    [SkippableTheory]
    [InlineData("sediment")]
    [InlineData("light")]
    public async Task EntityReadReturnsDescriptionsAndNamedRelations(string reference)
    {
        string? connection = Environment.GetEnvironmentVariable("LAPLACE_LEXICON_ACCEPTANCE_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connection), "Explicit seeded lexicon acceptance connection not supplied");
        await using var client = new SubstrateClient(connection);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var field = await client.LexiconAsync(reference,null,cancellation.Token);
        Assert.NotNull(field);
        Assert.Equal(2,field.Rounds);
        Assert.NotEmpty(field.Readings!);
        Assert.All(field.Readings!,row=>Assert.True(row.Side==1 && row.Witnesses>0 && row.ClaimIds.Count>0));
        Assert.DoesNotContain(field.Readings!,row=>row.Text.Contains("type of pointer that lemma",StringComparison.OrdinalIgnoreCase));
        Assert.All(field.Connections!,row=>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Relation));
            Assert.True(row.Relation.All(ch=>char.IsLetter(ch)||char.IsWhiteSpace(ch)));
            Assert.NotEqual("identifier vocabulary",row.Relation);
            Assert.DoesNotMatch(@"^[a-f0-9]{32}$",row.To);
            Assert.False(Uri.TryCreate(row.To,UriKind.Absolute,out _));
            Assert.True(row.Witnesses>0);
            Assert.NotNull(row.ToId);
        });
        var directory=Environment.GetEnvironmentVariable("LAPLACE_LIVE_ENTITY_EVIDENCE_DIRECTORY");
        if(!string.IsNullOrWhiteSpace(directory))
            await File.WriteAllTextAsync(Path.Combine(directory,reference+"-entity-candidate.json"),JsonSerializer.Serialize(field));
    }

    [SkippableFact]
    public async Task RecordedMeaningsAndExamplesReachBothSidesWithWitnessedConnections()
    {
        string? connection = Environment.GetEnvironmentVariable("LAPLACE_LEXICON_ACCEPTANCE_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connection), "Explicit seeded lexicon acceptance connection not supplied");
        await using var client = new SubstrateClient(connection);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var field = await client.LexiconAsync("love", "hate",cancellation.Token);
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
