using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class StructuredGrammarIngestTests
{
    private static readonly Hash128 Src =
        Hash128.OfCanonical("substrate/source/test/structured-ingest/v1");

    private sealed class NullGrammarWitness(string modalityId) : IGrammarWitness
    {
        public string ModalityId => modalityId;
        public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder builder) { }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task IngestFileAsync_TsvRows_CompletesWithoutCrash(int composeWorkers)
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-tsv-{Guid.NewGuid():N}.tsv");
        try
        {
            var lines = new[]
            {
                "1\tRelatedTo\t/c/en/dog\t/c/en/animal\t{}",
                "2\tIsA\t/c/en/poodle\t/c/en/dog\t{}",
                "3\tSynonym\t/c/en/happy\t/c/en/glad\t{}",
            };
            await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);

            var witness = new NullGrammarWitness("tsv");
            var changes = new List<SubstrateChange>();
            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                path, "tsv", Src, witness, batchSize: 2, witnessWeight: 1.0,
                batchLabelPrefix: "test", reportUnits: null, composeWorkers: composeWorkers))
            {
                changes.Add(change);
            }

            Assert.NotEmpty(changes);
            Assert.Equal(lines.Length, changes.Sum(c => c.Metadata.InputUnitsConsumed));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task IngestFileAsync_AcceptRowPreFilter_SkipsBeforeParse()
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-tsv-filter-{Guid.NewGuid():N}.tsv");
        try
        {
            await File.WriteAllTextAsync(path,
                "skip\trow\n" +
                "1\tRelatedTo\t/c/en/a\t/c/en/b\t{}\n");

            var witness = new NullGrammarWitness("tsv");
            var changes = new List<SubstrateChange>();
            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                path, "tsv", Src, witness, batchSize: 8, witnessWeight: 1.0,
                batchLabelPrefix: "test", reportUnits: null,
                acceptRow: line => !line.StartsWith("skip"u8),
                composeWorkers: 4))
            {
                changes.Add(change);
            }

            Assert.Single(changes);
            Assert.Equal(1, changes[0].Metadata.InputUnitsConsumed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
