using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Unicode;

public sealed class UnicodeDecomposer : IDecomposer
{
    public static readonly Hash128 Source = Hash128.OfCanonical("substrate/source/UnicodeDecomposer/v1");
    public static readonly Hash128 TrustClass = Hash128.OfCanonical("substrate/trust_class/StandardsDerived/v1");
    public static readonly Hash128 CodepointType = Hash128.OfCanonical("substrate/type/Codepoint/v1");

    private const string UnicodeVersion = "17.0.0";
    private const int DefaultBatch = 16384;

    private readonly string? _ucdxmlZip;
    private readonly string? _ducet;
    private CodepointRecord[]? _records;

    public UnicodeDecomposer(string? ucdxmlZip = null, string? ducet = null)
    {
        _ucdxmlZip = ucdxmlZip;
        _ducet = ducet;
    }

    public Hash128 SourceId => Source;
    public string SourceName => "UnicodeDecomposer";
    public int LayerOrder => 0;
    public Hash128 TrustClassId => TrustClass;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("Codepoint");
        return context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureComputed(context);
        int total = _records!.Length;
        int batch = options.BatchSize > 1 ? options.BatchSize : DefaultBatch;

        for (int start = 0; start < total; start += batch)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + batch, total);
            yield return BuildBatch(start, end);
            await Task.Yield();
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(UnicodeSeed.CodepointCount);

    public ValueTask DisposeAsync() { _records = null; return ValueTask.CompletedTask; }

    private SubstrateChange BuildBatch(int start, int end)
    {
        int n = end - start;
        var b = new SubstrateChangeBuilder(
            Source, $"codepoints/U+{start:X4}..U+{(end - 1):X4}",
            parentIntentId: null,
            entityCapacity: n, physicalityCapacity: n, attestationCapacity: 0);

        CodepointRecord[] recs = _records!;
        for (int cp = start; cp < end; cp++)
        {
            ref readonly CodepointRecord r = ref recs[cp];
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
                TrajectoryXyzm: null,
                NConstituents: 0,
                AlignmentResidual: 0.0,
                SourceDim: null,
                ObservedAtUnixUs: 0));
        }
        return b.Build();
    }

    private void EnsureComputed(IDecomposerContext context)
    {
        if (_records is not null) return;
        var (xml, duc) = ResolveSource(context);
        _records = UnicodeSeed.Compute(xml, duc);
    }

    private (string xml, string duc) ResolveSource(IDecomposerContext context)
    {
        string baseDir = context.EcosystemPath;
        string xml = _ucdxmlZip ?? Path.Combine(baseDir, "Public", UnicodeVersion, "ucdxml", "ucd.nounihan.flat.zip");
        string duc = _ducet ?? Path.Combine(baseDir, "Public", UnicodeVersion, "uca", "allkeys.txt");
        return (xml, duc);
    }
}
