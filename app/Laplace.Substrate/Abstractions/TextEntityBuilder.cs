using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Decomposers.Abstractions;

public sealed class TextEntityBuilder
{
    public static readonly Hash128 CodepointTypeId = EntityTypeRegistry.Codepoint;
    public static readonly Hash128 GraphemeTypeId = EntityTypeRegistry.Grapheme;
    public static readonly Hash128 WordTypeId = EntityTypeRegistry.Word;
    public static readonly Hash128 SentenceTypeId = EntityTypeRegistry.Sentence;
    public static readonly Hash128 DocumentTypeId = EntityTypeRegistry.Document;


    private readonly TierTree _tree;
    private readonly Hash128 _sourceId;
    private readonly byte[]? _existingBitmap;

    public TextEntityBuilder(TierTree tree, Hash128 sourceId, byte[]? existingBitmap = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _tree = tree;
        _sourceId = sourceId;
        _existingBitmap = existingBitmap;
    }

    public (ImmutableArray<EntityRow> Entities, ImmutableArray<PhysicalityRow> Physicalities) Build()
        => Build(IngestSizing.ResolveWorkingSetBudgetBytes());

    internal unsafe (ImmutableArray<EntityRow> Entities, ImmutableArray<PhysicalityRow> Physicalities) Build(
        long grant)
    {
        if (grant <= 0) throw new ArgumentOutOfRangeException(nameof(grant));
        int nodeCount = _tree.NodeCount;
        if (nodeCount == 0) return ([], []);
        if (_existingBitmap is { Length: > 0 }
            && _existingBitmap.LongLength < (nodeCount + 7L) / 8L)
            throw new ArgumentException("existing bitmap must cover every source tree node", nameof(_existingBitmap));

        using var stage = IntentStage.NewBounded(nodeCount, grant);
        if (!stage.EmitContentTree(_tree, _sourceId, _existingBitmap, out _))
            throw new InvalidOperationException("native content tree emission failed");
        // The same native owner supplies all identities, tiers, geometry and RLE
        // carriers; an atomic root uses its actual floor placement.
        // This synchronous interval accounts actual managed export allocations,
        // including temporary decoder/coverage collections and retained row arrays.
        // It is a conservative allocation reservation, not a live-heap estimate.
        long exportAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        var entities = CopyTupleParser.DecodeEntityRows([stage.TupleBuffer(IntentStageTable.Entities)])
            .ToImmutableArray();
        var physicalities = ImmutableArray.CreateBuilder<PhysicalityRow>(stage.PhysicalityCount);
        long nativeStageBytes = stage.AllocatedBytes;
        long managedExportBytes = checked(GC.GetAllocatedBytesForCurrentThread() - exportAllocationStart);
        long remaining = checked(grant - nativeStageBytes - managedExportBytes);
        if (remaining <= 0)
            throw new InvalidOperationException("native content stage and managed metadata exhausted the row-export allocation grant");
        stage.VisitPhysicalityRows(remaining, (inputs, origins, retainedCaptureBytes) =>
        {
            // The native capture and managed trajectory copies coexist. Reject
            // impossible trajectory payload before copying, then measure actual
            // row/array allocation during that same capture lifetime.
            long trajectoryPayloadBytes = 0;
            for (int i = 0; i < inputs.Length; ++i)
                trajectoryPayloadBytes = checked(trajectoryPayloadBytes
                    + checked((long)inputs[i].TrajectoryVertices * 4 * sizeof(double)));
            long available = checked(grant - nativeStageBytes - retainedCaptureBytes
                - (GC.GetAllocatedBytesForCurrentThread() - exportAllocationStart));
            if (trajectoryPayloadBytes > available)
                throw new InvalidOperationException("managed physicality trajectory export exceeded its allocation grant");
            for (int i = 0; i < inputs.Length; ++i)
            {
                var input = inputs[i];
                var origin = origins[i];
                if (origin.SourceStageIndex != 0 || origin.SourceRowIndex != (nuint)i)
                    throw new InvalidOperationException("native text physicality rows lost their source order");
                int width = checked((int)(input.TrajectoryVertices * 4));
                double[]? trajectory = width == 0 ? null
                    : new ReadOnlySpan<double>(input.Trajectory, width).ToArray();
                physicalities.Add(new PhysicalityRow(origin.PlacementId,
                    input.EntityId, _sourceId, (PhysicalityType)input.Type,
                    input.Coordinate[0], input.Coordinate[1], input.Coordinate[2], input.Coordinate[3],
                    input.HilbertIndex, trajectory, input.Constituents,
                    input.AlignmentResidualIsNull != 0 ? null : input.AlignmentResidual,
                    input.SourceDimIsNull != 0 ? null : input.SourceDim,
                    origin.ObservedAtUnixUs));
                long allocated = checked(GC.GetAllocatedBytesForCurrentThread() - exportAllocationStart);
                if (allocated > checked(grant - nativeStageBytes - retainedCaptureBytes))
                    throw new InvalidOperationException("managed physicality row export exceeded its allocation grant");
            }
        });
        return (entities, physicalities.MoveToImmutable());
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe int Resolver(
        uint atom, IntPtr userData,
        Hash128* outId, double* outCoord, Hilbert128* outHb)
    {
        var recs = CodepointPerfcache.Records;
        if (atom >= (uint)recs.Length) return -1;
        ref readonly var r = ref recs[(int)atom];
        *outId = r.Hash;
        outCoord[0] = r.CoordX; outCoord[1] = r.CoordY;
        outCoord[2] = r.CoordZ; outCoord[3] = r.CoordM;
        *outHb = r.Hilbert;
        return 0;
    }

    public static bool TryDecomposeRoot(
        byte[] canonical,
        out Hash128 rootId, out byte rootTier,
        out double cx, out double cy, out double cz, out double cm)
    {
        try
        {
            using var tree = TextDecomposer.Run(canonical);
            unsafe
            {
                HashComposer.RunWorkers(
                    tree, &Resolver, Math.Max(1, IngestTopology.Current.ComposeWorkers));
            }
            int nc = tree.NodeCount;
            if (nc == 0)
            {
                rootId = default; rootTier = 0;
                cx = cy = cz = cm = double.NaN;
                return false;
            }
            var root = tree.GetNode(tree.NaturalUnitIndex());
            rootId = root.Id; rootTier = root.Tier;
            unsafe { cx = root.Coord[0]; cy = root.Coord[1]; cz = root.Coord[2]; cm = root.Coord[3]; }
            return true;
        }
        catch (InvalidOperationException)
        {


            if (!CodepointPerfcache.IsLoaded) throw;
            rootId = default; rootTier = 0;
            cx = cy = cz = cm = double.NaN;
            return false;
        }
    }

    public static bool TryBuildRows(
        byte[] canonical, Hash128 sourceId,
        out ImmutableArray<EntityRow> entities,
        out ImmutableArray<PhysicalityRow> physicalities,
        out Hash128 rootId, out byte rootTier)
    {
        try
        {
            using var tree = TextDecomposer.Run(canonical);
            unsafe
            {
                HashComposer.RunWorkers(
                    tree, &Resolver, Math.Max(1, IngestTopology.Current.ComposeWorkers));
            }
            int nc = tree.NodeCount;
            if (nc == 0)
            {
                entities = ImmutableArray<EntityRow>.Empty;
                physicalities = ImmutableArray<PhysicalityRow>.Empty;
                rootId = default; rootTier = 0;
                return false;
            }
            var root = tree.GetNode(tree.NaturalUnitIndex());
            rootId = root.Id; rootTier = root.Tier;
            var (es, ps) = new TextEntityBuilder(tree, sourceId).Build();
            entities = es;
            physicalities = ps;
            return true;
        }
        catch (InvalidOperationException)
        {
            if (!CodepointPerfcache.IsLoaded) throw;
            entities = ImmutableArray<EntityRow>.Empty;
            physicalities = ImmutableArray<PhysicalityRow>.Empty;
            rootId = default; rootTier = 0;
            return false;
        }
    }

    public static bool TryBuildContentWitness(
        byte[] canonical, Hash128 sourceId, double witnessWeight,
        out ImmutableArray<EntityRow> entities,
        out ImmutableArray<PhysicalityRow> physicalities,
        out ImmutableArray<AttestationRow> attestations,
        out Hash128 rootId, out byte rootTier)
    {
        try
        {
            using var tree = TextDecomposer.Run(canonical);
            unsafe
            {
                HashComposer.RunWorkers(
                    tree, &Resolver, Math.Max(1, IngestTopology.Current.ComposeWorkers));
            }
            int nc = tree.NodeCount;
            if (nc == 0)
            {
                entities = ImmutableArray<EntityRow>.Empty;
                physicalities = ImmutableArray<PhysicalityRow>.Empty;
                attestations = ImmutableArray<AttestationRow>.Empty;
                rootId = default; rootTier = 0;
                return false;
            }
            var root = tree.GetNode(tree.NaturalUnitIndex());
            rootId = root.Id; rootTier = root.Tier;
            var (es, ps) = new TextEntityBuilder(tree, sourceId).Build();
            entities = es;
            physicalities = ps;
            // Pillar 3a: text emits its content DAG (entities + physicalities/trajectory) ONLY.
            // Sequence lives in the trajectory geometry; containment is containers_of + the
            // point-match; PRECEDES is a MODEL relation (token couplings from Q/K/V/O/gate/up/
            // down/norms), NOT text word-adjacency. Jamming word->word PRECEDES + CONTAINS onto
            // text was the error that produced millions of redundant attestations (the re-witness
            // grind) and duplicated what the geometry already holds losslessly. Deleted.
            _ = witnessWeight;
            attestations = ImmutableArray<AttestationRow>.Empty;
            return true;
        }
        catch (InvalidOperationException)
        {
            if (!CodepointPerfcache.IsLoaded) throw;
            entities = ImmutableArray<EntityRow>.Empty;
            physicalities = ImmutableArray<PhysicalityRow>.Empty;
            attestations = ImmutableArray<AttestationRow>.Empty;
            rootId = default; rootTier = 0;
            return false;
        }
    }
}
