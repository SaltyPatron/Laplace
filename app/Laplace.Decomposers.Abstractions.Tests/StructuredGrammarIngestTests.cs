using System.Text;
using System.Threading;
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

    [Fact]
    public async Task IngestFileAsync_MaxInputUnits_StopsEarly()
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-tsv-cap-{Guid.NewGuid():N}.tsv");
        try
        {
            var lines = Enumerable.Range(1, 20)
                .Select(i => $"{i}\tRelatedTo\t/c/en/a{i}\t/c/en/b{i}\t{{}}")
                .ToArray();
            await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);

            var witness = new NullGrammarWitness("tsv");
            var changes = new List<SubstrateChange>();
            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                path, "tsv", Src, witness, batchSize: 4, witnessWeight: 1.0,
                batchLabelPrefix: "test", reportUnits: null, maxInputUnits: 7,
                composeWorkers: 1))
            {
                changes.Add(change);
            }

            long consumed = changes.Sum(c => c.Metadata.InputUnitsConsumed);
            Assert.Equal(7, consumed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task IngestFileAsync_MaxInputUnits_ParallelStopsEarly(int composeWorkers)
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-tsv-cap-p-{Guid.NewGuid():N}.tsv");
        try
        {
            var lines = Enumerable.Range(1, 20)
                .Select(i => $"{i}\tRelatedTo\t/c/en/a{i}\t/c/en/b{i}\t{{}}")
                .ToArray();
            await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);

            var witness = new NullGrammarWitness("tsv");
            var changes = new List<SubstrateChange>();
            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                path, "tsv", Src, witness, batchSize: 4, witnessWeight: 1.0,
                batchLabelPrefix: "test", reportUnits: null, maxInputUnits: 7,
                composeWorkers: composeWorkers))
            {
                changes.Add(change);
            }

            long consumed = changes.Sum(c => c.Metadata.InputUnitsConsumed);
            Assert.Equal(7, consumed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
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

    // RFC-4180: a quoted field may contain a literal newline; the surrounding record is ONE row.
    // The tree-sitter csv grammar models this (quoted text = '"' (any-non-'"' | '""')* '"'); a raw
    // '\n' pre-split severs the record before the grammar can honor the quote. This is the front-door
    // bug: the splitter usurps the grammar's record-boundary job and gets it wrong.
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task IngestFileAsync_QuotedEmbeddedNewline_IsOneRecord(int composeWorkers)
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-csv-qnl-{Guid.NewGuid():N}.csv");
        try
        {
            // One logical record, four fields; field 3 = "line1\nline2".
            await File.WriteAllTextAsync(path, "a,b,\"line1\nline2\",c\n");

            var witness = new NullGrammarWitness("csv");
            var changes = new List<SubstrateChange>();
            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                path, "csv", Src, witness, batchSize: 8, witnessWeight: 1.0,
                batchLabelPrefix: "test", reportUnits: null, composeWorkers: composeWorkers))
            {
                changes.Add(change);
            }

            long consumed = changes.Sum(c => c.Metadata.InputUnitsConsumed);
            Assert.Equal(1, consumed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // Returns a uniform "present" or "absent" bitmap for every probe, and records the candidate
    // counts it was asked about so the test can assert the pipeline actually probes through the
    // existing entities_exist_bitmap contract.
    private sealed class UniformReader(bool present) : ISubstrateReader
    {
        public int Probes;
        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Probes);
            var bm = new byte[(candidates.Count + 7) / 8];
            if (present) Array.Fill(bm, (byte)0xFF);
            return Task.FromResult(bm);
        }
    }

    private static long ContentEntityCount(List<SubstrateChange> changes) =>
        changes.Sum(c => c.IntentStages.IsDefaultOrEmpty
            ? 0L
            : c.IntentStages.Sum(s => (long)s.EntityCount));

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task IngestFileAsync_PresentBitmap_SkipsAllContentEntities(int composeWorkers)
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-tsv-present-{Guid.NewGuid():N}.tsv");
        try
        {
            var lines = Enumerable.Range(1, 12)
                .Select(i => $"{i}\tRelatedTo\t/c/en/dog{i}\t/c/en/animal{i}\t{{}}")
                .ToArray();
            await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);
            var witness = new NullGrammarWitness("tsv");

            var baseline = new List<SubstrateChange>();
            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                path, "tsv", Src, witness, batchSize: 4, witnessWeight: 1.0,
                batchLabelPrefix: "test", reportUnits: null, composeWorkers: composeWorkers))
                baseline.Add(change);
            Assert.True(ContentEntityCount(baseline) > 0);

            // Every entity already present => the whole compose subtree is skipped upstream.
            var present = new UniformReader(present: true);
            var changes = new List<SubstrateChange>();
            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                path, "tsv", Src, witness, batchSize: 4, witnessWeight: 1.0,
                batchLabelPrefix: "test", reportUnits: null, composeWorkers: composeWorkers,
                containmentReader: present))
                changes.Add(change);

            Assert.True(present.Probes > 0, "containment probe must run through the pipeline");
            Assert.Equal(0, ContentEntityCount(changes));
            Assert.Equal(lines.Length, changes.Sum(c => c.Metadata.InputUnitsConsumed));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // Serial only: parallel batch grouping is nondeterministic (rows fan out across workers), so
    // per-batch dedup totals legitimately vary run to run; the exact-equality invariant holds for
    // the deterministic serial path. The parallel two-phase drain is covered by the present-bitmap
    // theory above (composeWorkers: 4).
    [Fact]
    public async Task IngestFileAsync_AbsentBitmap_EmitsSameAsNoReader()
    {
        const int composeWorkers = 1;
        string path = Path.Combine(Path.GetTempPath(), $"laplace-tsv-absent-{Guid.NewGuid():N}.tsv");
        try
        {
            var lines = Enumerable.Range(1, 12)
                .Select(i => $"{i}\tRelatedTo\t/c/en/dog{i}\t/c/en/animal{i}\t{{}}")
                .ToArray();
            await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);
            var witness = new NullGrammarWitness("tsv");

            var baseline = new List<SubstrateChange>();
            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                path, "tsv", Src, witness, batchSize: 4, witnessWeight: 1.0,
                batchLabelPrefix: "test", reportUnits: null, composeWorkers: composeWorkers))
                baseline.Add(change);

            var absent = new UniformReader(present: false);
            var changes = new List<SubstrateChange>();
            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                path, "tsv", Src, witness, batchSize: 4, witnessWeight: 1.0,
                batchLabelPrefix: "test", reportUnits: null, composeWorkers: composeWorkers,
                containmentReader: absent))
                changes.Add(change);

            Assert.Equal(ContentEntityCount(baseline), ContentEntityCount(changes));
            Assert.Equal(
                baseline.Sum(c => c.Metadata.InputUnitsConsumed),
                changes.Sum(c => c.Metadata.InputUnitsConsumed));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
