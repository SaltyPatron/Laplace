using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Unicode;

/// <summary>
/// Layer-0 decomposer for the universal T0 codepoint alphabet (#183, bounded
/// to the codepoint seed). Emits all 1,114,112 Unicode codepoints as T0
/// entities, each with the substrate-canonical CONTENT physicality whose
/// coordinate is the super-Fibonacci placement over DUCET collation rank
/// (ADR 0006). The entity id IS the BLAKE3-128 of the codepoint's UTF-8 bytes
/// — the universal ground every higher-tier Merkle DAG bottoms in.
///
/// <para>
/// The values come from the T0 perf-cache blob (the build-time sibling of this
/// DB seed per ADR 0006), read via <see cref="CodepointPerfcache"/> — the same
/// bytes the engine state machines and PG extension see (one source of truth).
/// This seed is the DB half; cross-verifying the two byte-for-byte is #49.
/// </para>
///
/// <para>
/// The supporting vocabulary, Unihan, emoji, segmentation-class attestation
/// cloud, and sequence entities (the full #183 ecosystem) layer on top of this
/// foundation and are not in this bounded seed.
/// </para>
///
/// <para>Determinism (RULES R7): same Unicode + UCA version ⇒ byte-identical
/// rows on every machine. No wall-clock in any row (observed_at = 0); the
/// substrate sets insert timestamps.</para>
/// </summary>
public sealed class UnicodeDecomposer : IDecomposer
{
    /// <summary>Source entity id — <c>BLAKE3("substrate/source/UnicodeDecomposer/v1")</c>.</summary>
    public static readonly Hash128 Source = Hash128.OfCanonical("substrate/source/UnicodeDecomposer/v1");

    /// <summary>Unicode is a standards body ⇒ Tier-2 StandardsDerived trust
    /// (bootstrapped by 10_bootstrap.sql.in per ADR 0044). Tier-1
    /// SubstrateMandate is reserved for the substrate-canonical source.</summary>
    public static readonly Hash128 TrustClass = Hash128.OfCanonical("substrate/trust_class/StandardsDerived/v1");

    /// <summary>The Codepoint T0 type entity, registered at init.</summary>
    public static readonly Hash128 CodepointType = Hash128.OfCanonical("substrate/type/Codepoint/v1");

    private const int DefaultBatch = 8192;

    private readonly string? _blobPath;

    /// <param name="perfcacheBlobPath">Explicit perf-cache blob path; when null
    /// the installed <c>/opt/laplace/share/laplace/laplace_t0_perfcache*.bin</c>
    /// is used.</param>
    public UnicodeDecomposer(string? perfcacheBlobPath = null) => _blobPath = perfcacheBlobPath;

    public Hash128 SourceId => Source;
    public string SourceName => "UnicodeDecomposer";
    public int LayerOrder => 0;
    public Hash128 TrustClassId => TrustClass;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        EnsureLoaded();
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("Codepoint");
        return context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureLoaded();
        int total = CodepointPerfcache.Count;
        int batch = options.BatchSize > 1 ? options.BatchSize : DefaultBatch;

        for (int start = 0; start < total; start += batch)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + batch, total);
            // BuildBatch is synchronous and holds the ref-struct span only
            // within its own frame — never across the yield.
            SubstrateChange change = BuildBatch(start, end);
            yield return change;
            await Task.Yield();   // cooperative: let the writer drain between batches
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        EnsureLoaded();
        return Task.FromResult<long?>(CodepointPerfcache.Count);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;  // perf-cache is process-wide; not ours to unload

    private static SubstrateChange BuildBatch(int start, int end)
    {
        int n = end - start;
        var b = new SubstrateChangeBuilder(
            Source, $"codepoints/U+{start:X4}..U+{(end - 1):X4}",
            parentIntentId: null,
            entityCapacity: n, physicalityCapacity: n, attestationCapacity: 0);

        ReadOnlySpan<CodepointRecord> records = CodepointPerfcache.Records;
        for (int cp = start; cp < end; cp++)
        {
            ref readonly CodepointRecord r = ref records[cp];
            Hash128 entityId = r.Hash;

            b.AddEntity(entityId, tier: 0, CodepointType, firstObservedBy: Source);

            Hash128 physId = PhysicalityId.Compute(
                entityId, Source, PhysicalityKind.Content,
                r.CoordX, r.CoordY, r.CoordZ, r.CoordM,
                ReadOnlySpan<double>.Empty);

            b.AddPhysicality(new PhysicalityRow(
                Id: physId,
                EntityId: entityId,
                SourceId: Source,
                Kind: PhysicalityKind.Content,
                CoordX: r.CoordX, CoordY: r.CoordY, CoordZ: r.CoordZ, CoordM: r.CoordM,
                HilbertIndex: r.Hilbert,
                TrajectoryXyzm: null,        // T0 atom — no decomposition trajectory
                NConstituents: 0,
                AlignmentResidual: 0.0,      // substrate-canonical, not a projection
                SourceDim: null,
                ObservedAtUnixUs: 0));       // timeless; substrate sets insert clock
        }
        return b.Build();
    }

    private void EnsureLoaded()
    {
        if (CodepointPerfcache.IsLoaded) return;
        CodepointPerfcache.Load(_blobPath ?? ResolveInstalledBlob());
    }

    private static string ResolveInstalledBlob()
    {
        const string share = "/opt/laplace/share/laplace";
        if (Directory.Exists(share))
        {
            string? hit = Directory.EnumerateFiles(share, "laplace_t0_perfcache*.bin")
                                   .OrderByDescending(p => p).FirstOrDefault();
            if (hit is not null) return hit;
        }
        throw new InvalidOperationException(
            "T0 perf-cache blob not found under /opt/laplace/share/laplace. Build + install " +
            "the engine (the laplace_t0_perfcache target installs it) or pass an explicit " +
            "path to the UnicodeDecomposer constructor.");
    }
}
