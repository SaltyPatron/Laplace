using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.UD;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using static Laplace.Decomposers.Abstractions.Tests.IngestPipelineTestHelpers;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class UdIngestPipelineTests
{
    private static readonly Hash128 UdSource = UDDecomposer.Source;

    static UdIngestPipelineTests() => LanguageReference.EnsureLoaded();

    [Fact]
    public async Task UdPipeline_BatchedProbe_OneDescentPerChunk()
    {
        const int sentenceCount = 8;
        var records = BuildFakeSentences(sentenceCount);
        var reader = new ProbeTrackingReader(present: false);
        var config = UdIngestSupport.PipelineConfig(UdSource, "ud-test", batchSentences: sentenceCount, reader);
        config = new IngestBatchConfig
        {
            SourceId = config.SourceId,
            BatchLabelPrefix = config.BatchLabelPrefix,
            BatchSize = config.BatchSize,
            ProbeChunkSize = sentenceCount,
            ContainmentReader = reader,
            EnableDeferredContentOnBuilder = false,
            EntityCapacity = config.EntityCapacity,
            PhysicalityCapacity = config.PhysicalityCapacity,
            AttestationCapacity = config.AttestationCapacity,
        };

        var handler = new UdIngestHandler(UdSource, new System.Collections.Concurrent.ConcurrentDictionary<string, byte>());
        await foreach (var _ in IngestBatchPipeline.RunAsync(
            new UdListRecordStream(records), handler, config))
        { }

        Assert.Equal(ExpectedDescentProbeChunks(sentenceCount, sentenceCount), reader.DescentProbeCalls);
        Assert.True(reader.FlatProbeCalls >= 1, "root bulk IN + tier1 flat completion");
    }

    [Fact]
    public async Task UdPipeline_PresentReader_ZeroEntities()
    {
        var records = BuildFakeSentences(6);
        var reader = new ProbeTrackingReader(present: true);
        var config = UdIngestSupport.PipelineConfig(UdSource, "ud-present", batchSentences: 6, reader);
        config = new IngestBatchConfig
        {
            SourceId = config.SourceId,
            BatchLabelPrefix = config.BatchLabelPrefix,
            BatchSize = config.BatchSize,
            ProbeChunkSize = 6,
            ContainmentReader = reader,
            EnableDeferredContentOnBuilder = false,
            EntityCapacity = config.EntityCapacity,
            PhysicalityCapacity = config.PhysicalityCapacity,
            AttestationCapacity = config.AttestationCapacity,
        };

        var baselineHandler = new UdIngestHandler(UdSource, new System.Collections.Concurrent.ConcurrentDictionary<string, byte>());
        var baseline = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new UdListRecordStream(records), baselineHandler, UdIngestSupport.PipelineConfig(UdSource, "ud-base", batchSentences: 6, null)))
            baseline.Add(c);
        Assert.True(ContentEntityCount(baseline) > 0);

        var handler = new UdIngestHandler(UdSource, new System.Collections.Concurrent.ConcurrentDictionary<string, byte>());
        var changes = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new UdListRecordStream(records), handler, config))
            changes.Add(c);

        Assert.True(reader.DescentProbeCalls >= 1, "present ingest must batched-probe sentence content trees");
        Assert.True(ContentEntityCount(changes) <= ContentEntityCount(baseline),
            "present bitmap should not increase staged content entities vs fresh ingest");
        Assert.True(AttestationCount(changes) > 0, "present sentences must still emit UD attestations");
        Assert.Equal(records.Count, changes.Sum(c => c.Metadata.InputUnitsConsumed));
    }

    [Fact]
    public async Task UdConlluStream_ParsesTempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-ud-{Guid.NewGuid():N}.conllu");
        try
        {
            await File.WriteAllTextAsync(path, FakeConllu(3), Encoding.UTF8);
            string langCode = "en";
            Hash128 langId = LanguageReference.Resolve(langCode);
            var stream = new UdConlluFileStream(path, langId, langCode);

            int count = 0;
            await foreach (var rec in stream.RecordsAsync())
            {
                Assert.NotEmpty(rec.Sentence.Tokens);
                count++;
            }
            Assert.Equal(3, count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task DescribeInputAsync_MaxInputUnits_ReturnsCapWithoutScanningSentences()
    {
        string dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "laplace-ud-inv-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string treebanks = Path.Combine(dir, "ud-treebanks-v2.17");
            Directory.CreateDirectory(treebanks);
            await File.WriteAllTextAsync(
                Path.Combine(treebanks, "en_test.conllu"), FakeConllu(5), Encoding.UTF8);

            var dec = new UDDecomposer();
            var inv = await dec.DescribeInputAsync(
                new UdTempContext(dir),
                DecomposerOptions.Default with { MaxInputUnits = 50_000 });

            Assert.NotNull(inv);
            Assert.Equal(50_000, inv!.TotalInputUnits);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private sealed class UdTempContext(string ecosystemPath) : IDecomposerContext
    {
        public string EcosystemPath => ecosystemPath;
        public ISubstrateWriter Writer => throw new NotSupportedException();
        public ISubstrateReader Reader => throw new NotSupportedException();
        public Microsoft.Extensions.Logging.ILogger Logger =>
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public string SubstrateVersion => "test";
    }

    private static List<UdIngestRecord> BuildFakeSentences(int count)
    {
        string langCode = "en";
        Hash128 langId = LanguageReference.Resolve(langCode);
        var records = new List<UdIngestRecord>(count);
        for (int i = 1; i <= count; i++)
            records.Add(new UdIngestRecord(FakeSentence(i), langId, langCode));
        return records;
    }

    private static UdSentence FakeSentence(int n)
    {
        var form = Encoding.UTF8.GetBytes($"word{n}");
        var beta = Encoding.UTF8.GetBytes($"beta{n}");
        var tokens = new List<UdToken>
    {
      new(1, "1", form, form, true, "NOUN", "_", ["Number=Sing"], 0, "root", "_", "_"),
      new(2, "2", beta, beta, true, "VERB", "_", [], 1, "compound", "_", $"Gloss=gloss{n}"),
    };
        return new UdSentence(Encoding.UTF8.GetBytes($"ud probe sentence {n}"), tokens, [], 2);
    }

    private static string FakeConllu(int sentences) =>
      string.Join("\n\n", Enumerable.Range(1, sentences).Select(FakeConlluSentence));

    private static string FakeConlluSentence(int n) =>
      $"# text = ud probe sentence {n}\n" +
      $"1\tword{n}\tword{n}\tNOUN\t_\tNumber=Sing\t0\troot\t_\t_\n" +
      $"2\tbeta{n}\tbeta{n}\tVERB\t_\t_\t1\tcompound\t_\tGloss=gloss{n}\n";
}
