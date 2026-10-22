using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

// These tests deliberately unload the process-wide floor. Other collections
// must finish before they run, including lock-free floor readers.
[CollectionDefinition("NativeRecipeLifetime", DisableParallelization = true)]
public sealed class NativeRecipeLifetimeCollection { }

[Collection("NativeRecipeLifetime")]
public sealed class NativeRecipeConcurrencyTests
{
    private static readonly byte[] Input = Encoding.UTF8.GetBytes(
        string.Concat(Enumerable.Repeat("dog;noun;3;0.25\ndog;noun;7;0.75\ncat;noun;2;0\n", 20)));

    [Fact]
    public async Task IndependentParsersProduceExactSeriesWhileTheSharedNativeGateIsOccupied()
    {
        byte[] program = Program();
        Hash128 witness = SourceWitness.Id("ConcurrentSeries", "1");
        using var first = NativeRecipeStream.Open(program, witness, 1);
        using var second = NativeRecipeStream.Open(program, witness, 1);
        using var releaseGate = new ManualResetEventSlim();
        var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateOwner = Task.Factory.StartNew(() =>
        {
            lock (LaplaceCoreGate.Native)
            {
                gateEntered.SetResult();
                releaseGate.Wait();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<(AttestationRow[] Rows, ulong Completed)[]> both;
        bool completed;
        try
        {
            await gateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var a = Task.Factory.StartNew(() => Read(first, 1), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var b = Task.Factory.StartNew(() => Read(second, 4096), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            both = Task.WhenAll(a, b);
            completed = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(10))) == both;
        }
        finally
        {
            releaseGate.Set();
            await gateOwner;
        }
        // Always join both owners before disposing their native stream state,
        // including the regression where both workers waited on the global gate.
        var results = await both;
        Assert.True(completed, "Independent recipe parsing waited for the process-wide native gate.");
        Assert.Equal(60UL, results[0].Completed);
        Assert.Equal(results[0].Completed, results[1].Completed);
        Assert.Equal(results[0].Rows, results[1].Rows);
        Assert.Equal(2, results[0].Rows.Length);
        Assert.Equal(240, results[0].Rows.Sum(r => r.ObservationCount));
        Assert.Equal(120_000_000_000, results[0].Rows.Sum(r => r.SumScoreFp1e9!.Value));
    }

    [Fact]
    public void EveryOpenStreamRetainsTheFloorUntilItsFinalRelease()
    {
        byte[] program = Program();
        var first = NativeRecipeStream.Open(program, default, 1);
        var second = NativeRecipeStream.Open(program, default, 1);
        try
        {
            Assert.Contains("2 native recipe stream(s)",
                Assert.Throws<InvalidOperationException>(CodepointPerfcache.Unload).Message);
            first.Dispose();
            Assert.Contains("1 native recipe stream(s)",
                Assert.Throws<InvalidOperationException>(CodepointPerfcache.Unload).Message);
            Assert.NotEmpty(Read(second, 7).Rows);
            second.Dispose();
            CodepointPerfcache.Unload();
            Assert.False(CodepointPerfcache.IsLoaded);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            CodepointPerfcache.LoadDefault();
        }
    }

    [Fact]
    public void RejectedProgramReleasesItsFloorLease()
    {
        CodepointPerfcache.LoadDefault();
        try
        {
            Assert.Throws<InvalidDataException>(() => NativeRecipeStream.Open([0, 0, 0, 0], default, 1));
            CodepointPerfcache.Unload();
            Assert.False(CodepointPerfcache.IsLoaded);
        }
        finally { CodepointPerfcache.LoadDefault(); }
    }

    private static byte[] Program() => NativeRecipeCompiler.Compile(new SemanticSourceRecipe(
        "ConcurrentSeries", "1", "laplace/native-streaming-delimited-recipe/v1", "series/tsv",
        [
            new("record/@id", "id", SourceValueKind.Identity, SourceFieldDisposition.Identity),
            new("record/@aClaim", "claim", SourceValueKind.Text, SourceFieldDisposition.Testimony,
                RelationName: "SeriesClaim", Aggregate: true),
            new("record/@bGames", "games", SourceValueKind.Integer, SourceFieldDisposition.Calculation,
                ObservationOf: "record/@aClaim"),
            new("record/@cScore", "score", SourceValueKind.ExactNumber, SourceFieldDisposition.Calculation,
                ScoreOf: "record/@aClaim"),
        ], providerRoutes: [new("record", "", "record",
            new(SourceSubjectBindingKind.ContentField, IdentityField: "id"), [])],
        delimitedSyntax: new("record", ["id", "aClaim", "bGames", "cScore"], Separator: ";")));

    private static (AttestationRow[] Rows, ulong Completed) Read(NativeRecipeStream stream, int feedBytes)
    {
        using var empty = IntentStage.New(0);
        var rows = new List<AttestationRow>();
        ulong completed = 0;
        for (int offset = 0; offset < Input.Length; offset += feedBytes)
        {
            int count = Math.Min(feedBytes, Input.Length - offset);
            stream.Feed(Input.AsSpan(offset, count), offset + count == Input.Length);
            while (true)
            {
                using var stage = stream.Drain(100_000, 64 * 1024 * 1024, out ulong records);
                completed += records;
                if (stage is null) break;
                Hash128 digest = stage.SemanticDigest();
                IntentStage[] parts = stage.Partition(2);
                try
                {
                    Assert.Equal(digest, IntentStage.SemanticDigestBatch(parts));
                    foreach (var part in parts)
                        CopyTupleParser.DecodeAttestations([part.TupleBuffer(IntentStageTable.Attestations)], rows);
                }
                finally { foreach (var part in parts) part.Dispose(); }
            }
        }
        return (rows.Select(r => r with { LastObservedAtUnixUs = 0 })
            .OrderBy(r => r.Id.ToString(), StringComparer.Ordinal).ToArray(), completed);
    }
}
