using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

[Collection("GrammarPerfcache")]
public sealed class NativeRecipeSeriesTests
{
    [Theory]
    [InlineData(1, 256)]
    [InlineData(65536, 100000)]
    public void AggregatedSeriesPreservesEveryRecordsCountAndFixedPointScore(int feedBytes, int outputRows)
    {
        var rows = Read("dog;noun;3;0.25\ndog;noun;7;0.75\ndog;noun;2;0\n", true,
            feedBytes, outputRows);
        var claim = Assert.Single(rows);
        Assert.Equal(RelationTypeRegistry.RelationTypeId("SeriesClaim"), claim.TypeId);
        Assert.Equal(12, claim.ObservationCount);
        Assert.Equal(6_000_000_000, claim.SumScoreFp1e9);
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("99999999999999999999999999")]
    public void OutOfRangeObservationCountIsRejectedInsteadOfSaturating(string count)
    {
        var error = Assert.Throws<InvalidDataException>(() => Read($"dog;noun;{count};0\n", false));
        Assert.Contains("invalid observation count", error.Message);
        Assert.Contains(count, error.Message);
    }

    [Fact]
    public void FixedPointScoreOverflowIsRejectedBeforeAStageIsReturned()
    {
        var error = Assert.Throws<InvalidDataException>(() => Read("dog;noun;9223372037;1\n", false));
        Assert.Contains("score sum overflow", error.Message);
    }

    [Fact]
    public void AggregateCountOverflowIsRejectedWithItsRecordIdentity()
    {
        var error = Assert.Throws<InvalidDataException>(() => Read(
            "dog;noun;9223372036854775807;0\ndog;noun;1;0\n", true));
        Assert.Contains("aggregate observation count overflow", error.Message);
        Assert.Contains("record", error.Message);
    }

    private static List<AttestationRow> Read(string input, bool aggregate, int feedBytes = 65536,
        int outputRows = 100000)
    {
        CodepointPerfcache.LoadDefault();
        var recipe = new SemanticSourceRecipe("SeriesValidation", "1",
            "laplace/native-streaming-delimited-recipe/v1", "series/tsv",
            [
                new("record/@id", "id", SourceValueKind.Identity, SourceFieldDisposition.Identity),
                new("record/@aClaim", "claim", SourceValueKind.Text, SourceFieldDisposition.Testimony,
                    RelationName: "SeriesClaim", Aggregate: aggregate),
                new("record/@bGames", "games", SourceValueKind.Integer, SourceFieldDisposition.Calculation,
                    ObservationOf: "record/@aClaim"),
                new("record/@cScore", "score", SourceValueKind.ExactNumber, SourceFieldDisposition.Calculation,
                    ScoreOf: "record/@aClaim"),
            ],
            providerRoutes: [new("record", "", "record",
                new(SourceSubjectBindingKind.ContentField, IdentityField: "id"), [])],
            delimitedSyntax: new("record", ["id", "aClaim", "bGames", "cScore"], Separator: ";"));
        using var stream = NativeRecipeStream.Open(NativeRecipeCompiler.Compile(recipe),
            SourceWitness.Id(recipe.Authority, recipe.Release), 1);
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        var rows = new List<AttestationRow>();
        for (int offset = 0; offset < bytes.Length; offset += feedBytes)
        {
            int count = Math.Min(feedBytes, bytes.Length - offset);
            stream.Feed(bytes.AsSpan(offset, count), offset + count == bytes.Length);
            while (true)
            {
                using var stage = stream.Drain(outputRows, 64 * 1024 * 1024, out _);
                if (stage is null) break;
                CopyTupleParser.DecodeAttestations([stage.TupleBuffer(IntentStageTable.Attestations)], rows);
            }
        }
        return rows;
    }
}
