using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public sealed class TextEntityBuilder
{
    public static readonly Hash128 CodepointTypeId = EntityTypeRegistry.Codepoint;
    public static readonly Hash128 GraphemeTypeId  = EntityTypeRegistry.Grapheme;
    public static readonly Hash128 WordTypeId      = EntityTypeRegistry.Word;
    public static readonly Hash128 SentenceTypeId  = EntityTypeRegistry.Sentence;
    public static readonly Hash128 DocumentTypeId  = EntityTypeRegistry.Document;

    public static readonly Hash128 PrecedesTypeId = RelationTypeRegistry.RelationTypeId("PRECEDES");

    private const byte WordTier     = 2;
    private const byte SentenceTier = 3;

    private readonly TierTree  _tree;
    private readonly Hash128   _sourceId;
    private readonly byte[]?   _existingBitmap;
    private readonly HashSet<Hash128> _emittedIds = new();

    private readonly ImmutableArray<EntityRow>.Builder       _entities;
    private readonly ImmutableArray<PhysicalityRow>.Builder  _physicalities;

    public TextEntityBuilder(TierTree tree, Hash128 sourceId, byte[]? existingBitmap = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _tree           = tree;
        _sourceId       = sourceId;
        _existingBitmap = existingBitmap;
        int n           = tree.NodeCount;
        _entities       = ImmutableArray.CreateBuilder<EntityRow>(n);
        _physicalities  = ImmutableArray.CreateBuilder<PhysicalityRow>(n);
    }

    public (ImmutableArray<EntityRow> Entities, ImmutableArray<PhysicalityRow> Physicalities) Build()
    {
        int nodeCount = _tree.NodeCount;
        if (nodeCount == 0)
            return (_entities.ToImmutable(), _physicalities.ToImmutable());

        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

        if (_existingBitmap is { Length: > 0 })
        {
            var novelIdx = new uint[nodeCount];
            int novelCount = MerkleDedup.TrunkShortcircuit(_tree, _existingBitmap, novelIdx);
            for (int i = 0; i < novelCount; i++)
                EmitNode(novelIdx[i], nowUs);
        }
        else
        {
            for (uint idx = 0; idx < (uint)nodeCount; idx++)
                EmitNode(idx, nowUs);
        }

        return (_entities.ToImmutable(), _physicalities.ToImmutable());
    }

    private void EmitNode(uint idx, long nowUs)
    {
        var node = _tree.GetNode(idx);

        if (node.Tier == 0) return;
        if (!_tree.ShouldEmitCompositional(idx)) return;

        if (!_emittedIds.Add(node.Id)) return;

        var typeId = TierTypeId(node.Tier);
        _entities.Add(new EntityRow(node.Id, node.Tier, typeId, _sourceId));

        double[]? trajectoryXyzm = null;
        int nConstituents = 0;

        if (node.ChildCount > 0)
        {
            var childIds = new Hash128[node.ChildCount];
            var childFlags = new ulong[node.ChildCount];
            for (uint ci = 0; ci < node.ChildCount; ci++)
            {
                
                
                var child = _tree.GetNode(_tree.CollapseIndex(node.FirstChildIdx + ci));
                childIds[ci] = child.Id;
                childFlags[ci] = Trajectory.VertexFlags(
                    child.Tier, hasAtom: child.Tier == 0, atom: child.Atom);
            }
            trajectoryXyzm = Trajectory.Build(childIds, childFlags);
            nConstituents  = (int)node.ChildCount;
        }

        double cx, cy, cz, cm;
        unsafe { cx = node.Coord[0]; cy = node.Coord[1]; cz = node.Coord[2]; cm = node.Coord[3]; }

        var physId = PhysicalityId.Compute(
            node.Id, PhysicalityType.Content,
            cx, cy, cz, cm,
            trajectoryXyzm ?? ReadOnlySpan<double>.Empty);

        _physicalities.Add(new PhysicalityRow(
            Id:                physId,
            EntityId:          node.Id,
            SourceId:          _sourceId,
            Type:              PhysicalityType.Content,
            CoordX:            cx,
            CoordY:            cy,
            CoordZ:            cz,
            CoordM:            cm,
            HilbertIndex:      node.Hilbert,
            TrajectoryXyzm:    trajectoryXyzm,
            NConstituents:     nConstituents,
            AlignmentResidual: null,
            SourceDim:         null,
            ObservedAtUnixUs:  nowUs));
    }

    private static Hash128 TierTypeId(byte tier) => tier switch
    {
        0 => CodepointTypeId,
        1 => GraphemeTypeId,
        2 => WordTypeId,
        3 => SentenceTypeId,
        _ => DocumentTypeId,
    };

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
            unsafe { HashComposer.Run(tree, &Resolver); }
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
            unsafe { HashComposer.Run(tree, &Resolver); }
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
            unsafe { HashComposer.Run(tree, &Resolver); }
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
            attestations = BuildDistributionalAttestations(tree, sourceId, witnessWeight);
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

    public static ImmutableArray<AttestationRow> BuildDistributionalAttestations(
        TierTree tree, Hash128 sourceId, double witnessWeight)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var precedes = new Dictionary<(Hash128 A, Hash128 B), long>();
        var contentMemo = new Dictionary<Hash128, bool>();
        var content = new List<Hash128>();
        int n = tree.NodeCount;
        for (uint idx = 0; idx < (uint)n; idx++)
        {
            var node = tree.GetNode(idx);
            if (node.Tier != SentenceTier || node.ChildCount < 2) continue;

            content.Clear();
            for (uint ci = 0; ci < node.ChildCount; ci++)
            {
                uint childIdx = node.FirstChildIdx + ci;
                var child = tree.GetNode(childIdx);
                if (child.Tier != WordTier) continue;
                if (IsContentWord(tree, childIdx, contentMemo)) content.Add(child.Id);
            }
            for (int i = 1; i < content.Count; i++)
            {
                var key = (content[i - 1], content[i]);
                precedes.TryGetValue(key, out long c);
                precedes[key] = c + 1;
            }
        }

        if (precedes.Count == 0) return ImmutableArray<AttestationRow>.Empty;

        
        
        
        var contextId = tree.GetNode(tree.NaturalUnitIndex()).Id;
        var rows = ImmutableArray.CreateBuilder<AttestationRow>(precedes.Count);
        foreach (var (pair, count) in precedes)
        {
            long sumScore = checked(count * Glicko2.FpScale);
            rows.Add(NativeAttestation.Aggregated(
                pair.A, PrecedesTypeId, pair.B, sourceId, contextId: contextId,
                games: count, sumScoreFp1e9: sumScore, witnessWeight: witnessWeight));
        }
        return rows.ToImmutable();
    }

    private static bool IsContentWord(TierTree tree, uint idx, Dictionary<Hash128, bool> memo)
    {
        var node = tree.GetNode(idx);
        if (memo.TryGetValue(node.Id, out bool cached)) return cached;
        bool result = HasAlphanumericLeaf(tree, idx);
        memo[node.Id] = result;
        return result;
    }

    private static bool HasAlphanumericLeaf(TierTree tree, uint idx)
    {
        var node = tree.GetNode(idx);
        if (node.Tier == 0)
            return Rune.IsValid((int)node.Atom)
                && Rune.IsLetterOrDigit(new Rune((int)node.Atom));
        for (uint ci = 0; ci < node.ChildCount; ci++)
            if (HasAlphanumericLeaf(tree, node.FirstChildIdx + ci)) return true;
        return false;
    }
}
