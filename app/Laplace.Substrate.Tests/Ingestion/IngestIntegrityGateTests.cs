using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Abstractions.Tests;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Ingestion.Tests;

/// <summary>
/// I2/I5: operator integrity gates — no stub decomposers, empty ingest must fail closed.
/// </summary>
[Trait("Tier", "db")]
public sealed class IngestIntegrityGateTests : IClassFixture<LocalPgFixture>, IAsyncLifetime
{
    private readonly LocalPgFixture _pg;

    public IngestIntegrityGateTests(LocalPgFixture pg) => _pg = pg;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void DispatchTable_HasNoImageOrAudioStubRoutes()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var dispatch = Path.Combine(repoRoot, "app", "Laplace.Cli", "IngestDispatchTable.cs");
        var text = File.ReadAllText(dispatch);
        Assert.DoesNotContain("ImageDecomposer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AudioDecomposer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"image\"]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"audio\"]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DecomposerProjects_HaveNoImageOrAudioStubFiles()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var image = Path.Combine(repoRoot, "app", "Laplace.Decomposers", "Image", "ImageDecomposer.cs");
        var audio = Path.Combine(repoRoot, "app", "Laplace.Decomposers", "Audio", "AudioDecomposer.cs");
        Assert.False(File.Exists(image), "ImageDecomposer stub must be removed");
        Assert.False(File.Exists(audio), "AudioDecomposer stub must be removed");
    }

    [Fact]
    public void SeedStep_UnknownStepExitsThreeUnlessSkipVerify()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var seedStep = Path.Combine(repoRoot, "scripts", "win", "seed-step.cmd");
        var text = File.ReadAllText(seedStep);
        Assert.Contains("unknown seed step", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("goto unknown_step", text, StringComparison.Ordinal);
        Assert.Contains(":unknown_step", text, StringComparison.Ordinal);
        Assert.Contains("exit /b 3", text, StringComparison.Ordinal);
    }

    private sealed class EmptyYieldDecomposer : IDecomposer
    {
        private readonly long _declaredUnits;

        public EmptyYieldDecomposer(long declaredUnits, Hash128 sourceId)
        {
            _declaredUnits = declaredUnits;
            SourceId = sourceId;
        }

        public Hash128 SourceId { get; }
        public string SourceName => "EmptyYieldTest";
        public int LayerOrder => 2;
        public Hash128 TrustClassId =>
            SubstrateCanonicalIds.TrustClass("SubstrateMandate");

        public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
            IDecomposerContext context,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(_declaredUnits);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task IngestRunner_DeclaredInputWithZeroApplied_Throws()
    {
        var sourceId = SubstrateCanonicalIds.OfVersioned("source", "EmptyYieldTest", "gate");
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var runner = new IngestRunner(writer, reader, NullLoggerFactory.Instance);
        var decomposer = new EmptyYieldDecomposer(42, sourceId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(decomposer, IngestRunOptions.Default with { SkipSourceCompletion = true }));

        Assert.Contains("ingested 0", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EmptyYieldTest", ex.Message, StringComparison.Ordinal);
    }
}
