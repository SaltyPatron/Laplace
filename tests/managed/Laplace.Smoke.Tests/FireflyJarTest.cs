namespace Laplace.Smoke.Tests;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core;
using Laplace.Core.Abstractions;
using Laplace.Decomposers.Abstractions;
using Laplace.Pipeline;
using Laplace.Pipeline.Abstractions;

using Xunit;

/// <summary>
/// Validates FireflyJar.StoreAsync emits a PhysicalityRecord with the
/// firefly_s3_extracted physicality_type_hash, the correct entity + source,
/// and the position as a single-vertex Geometry. The retrieve side is
/// PG-backed and not exercised here (Phase 1.11).
/// </summary>
public class FireflyJarTest
{
    [Fact]
    public async Task StoreAsync_EmitsPhysicalityRecord_WithFireflyTypeAndSourceAndPosition()
    {
        var hashing  = new IdentityHashing();
        var pool     = new CodepointPool(hashing);
        var resolver = new ConceptEntityResolver(pool, hashing);

        var sink = new PhysicalityRecorder();
        var jar  = new FireflyJar(resolver, sink);

        var substrateEntity = resolver.Resolve("test_token_dog");
        var modelEntity     = resolver.Resolve("test_minilm");
        var position        = new Point4D(0.5, 0.5, 0.5, 0.5).Normalized(); // on S³

        await jar.StoreAsync(substrateEntity, modelEntity, position, CancellationToken.None);

        Assert.Single(sink.Records);
        var rec = sink.Records[0];

        Assert.Equal(
            resolver.Resolve("firefly_s3_extracted").ToString(),
            rec.PhysicalityTypeHash.ToString());
        Assert.Equal(substrateEntity.ToString(), rec.EntityHash.ToString());
        Assert.NotNull(rec.SourceHash);
        Assert.Equal(modelEntity.ToString(), rec.SourceHash.Value.ToString());
        Assert.Single(rec.Geometry);
        Assert.Equal(position.X, rec.Geometry[0].X);
        Assert.Equal(position.Y, rec.Geometry[0].Y);
        Assert.Equal(position.Z, rec.Geometry[0].Z);
        Assert.Equal(position.W, rec.Geometry[0].W);
    }

    [Fact]
    public async Task GetForAsync_NotImplemented_UntilLivePgQueryLayerLands()
    {
        var hashing  = new IdentityHashing();
        var pool     = new CodepointPool(hashing);
        var resolver = new ConceptEntityResolver(pool, hashing);
        var sink     = new PhysicalityRecorder();
        var jar      = new FireflyJar(resolver, sink);

        await Assert.ThrowsAsync<System.NotImplementedException>(
            () => jar.GetForAsync(resolver.Resolve("any_entity"), CancellationToken.None));
    }

    private sealed class PhysicalityRecorder : IPhysicalityEmission
    {
        public List<PhysicalityRecord> Records { get; } = new();
        public ValueTask EmitAsync(PhysicalityRecord record, CancellationToken cancellationToken)
        { Records.Add(record); return ValueTask.CompletedTask; }
    }
}
