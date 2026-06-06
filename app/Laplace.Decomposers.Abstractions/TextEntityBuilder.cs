using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Converts a hash-composed <see cref="TierTree"/> (after
/// <see cref="HashComposer.Run"/> has filled all node IDs) into
/// <see cref="EntityRow"/> + <see cref="PhysicalityRow"/> collections.
///
/// <para>Deduplication layers applied:</para>
/// <list type="number">
///   <item>Level 1 — within-intent: <c>HashSet&lt;Hash128&gt;</c> prevents
///     duplicate entity rows when two tree paths reference the same entity
///     (e.g. a repeated word appearing as a child of two different sentence
///     nodes in the same intent).</item>
///   <item>Level 2 — cross-intent bitmap: if <paramref name="existingBitmap"/>
///     is non-null, <see cref="MerkleDedup.TrunkShortcircuit"/> prunes
///     subtrees already present in the substrate — relying on the
///     SubstrateCRUD invariant that parent-presence implies
///     descendant-presence. Pass <c>null</c> to skip; rely on DB-level
///     <c>ON CONFLICT DO NOTHING</c> as the backstop.</item>
/// </list>
///
/// <para>Trajectories are RLE-compressed via
/// <see cref="Trajectory.BuildRle"/> — one vertex per run of consecutive
/// identical child IDs. <c>NConstituents</c> in each physicality row
/// records the original uncompressed child count.</para>
///
/// <para>Caller must have called <see cref="TierTree.FinalizeParents"/> and
/// <see cref="HashComposer.Run"/> before constructing this builder.</para>
/// </summary>
public sealed class TextEntityBuilder
{
    // Canonical substrate type IDs for each text tier — same formulas used
    // by UnicodeDecomposer (T0) and bootstrapped by SubstrateCanonical.
    public static readonly Hash128 CodepointTypeId = Hash128.OfCanonical("substrate/type/Codepoint/v1");
    public static readonly Hash128 GraphemeTypeId  = Hash128.OfCanonical("substrate/type/Grapheme/v1");
    public static readonly Hash128 WordTypeId      = Hash128.OfCanonical("substrate/type/Word/v1");
    public static readonly Hash128 SentenceTypeId  = Hash128.OfCanonical("substrate/type/Sentence/v1");
    public static readonly Hash128 DocumentTypeId  = Hash128.OfCanonical("substrate/type/Document/v1");

    // The DISTRIBUTIONAL witness kinds (bootstrap-seeded). Adjacent within-sentence
    // word order is the only thing prose can HONESTLY attest beyond its mechanical
    // decomposition — usage, never meaning. a PRECEDES b ⇔ b FOLLOWS a.
    /* ONE adjacency arena (registry rule 3): "A PRECEDES B"; the inverse
     * ("B follows A") is the reverse query (consensus_in on this arena), never
     * a second arena — the FOLLOWS twin doubled the testimony and was retired
     * 2026-06-05 (FOLLOWS is now a flip-alias in the registry). */
    public static readonly Hash128 PrecedesTypeId = RelationTypeRegistry.RelationTypeId("PRECEDES");

    // The text tiers (match TierTypeId / the histogram): 2=Word, 3=Sentence.
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

    /// <summary>Emit entity + physicality rows for all novel nodes in the
    /// tree. Call once; subsequent calls add duplicates.</summary>
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

        // Tier-0 codepoints are the foundational SEEDED atoms — UnicodeDecomposer
        // seeds them once (with perf-cache-derived coord/Hilbert), independently of
        // this builder. Content paths REFERENCE them by id inside grapheme/word
        // trajectories; re-emitting here would create a duplicate per-source codepoint
        // physicality for every text that uses them. Skip — the trajectory child-ids
        // still point at the seeded entities.
        if (node.Tier == 0) return;

        // Level 1 dedup: skip if already emitted within this intent
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
                var child = _tree.GetNode(node.FirstChildIdx + ci);
                childIds[ci] = child.Id;
                // In-band constituent type + atom (mantissa.h flag layout):
                // a render/walk reads tier + codepoint from the vertex itself —
                // no entities/codepoint_render join per leaf (2026-06-05).
                childFlags[ci] = Trajectory.VertexFlags(
                    child.Tier, hasAtom: child.Tier == 0, atom: child.Atom);
            }
            // Non-RLE: ONE vertex per constituent. The trajectory codec's decode
            // (trajectory_constituents) does NOT expand run_length, so BuildRle is
            // LOSSY through reconstruction — a repeated child (the 'l's in "hello")
            // RLE-collapses to one vertex and can't be recovered. Build keeps every
            // constituent as its own vertex → bit-perfect round-trip. The engine emits
            // a POINT for a single vertex and a LINESTRING for ≥2 (both valid GEOMETRY
            // ZM), so a single-constituent node carries an honest trajectory, never NULL.
            trajectoryXyzm = Trajectory.Build(childIds, childFlags);
            nConstituents  = (int)node.ChildCount;
        }

        double cx, cy, cz, cm;
        unsafe { cx = node.Coord[0]; cy = node.Coord[1]; cz = node.Coord[2]; cm = node.Coord[3]; }

        var physId = PhysicalityId.Compute(
            node.Id, _sourceId, PhysicalityType.Content,
            cx, cy, cz, cm,
            trajectoryXyzm ?? ReadOnlySpan<double>.Empty);

        _physicalities.Add(new PhysicalityRow(
            Id:                physId,
            EntityId:          node.Id,
            SourceId:          _sourceId,
            Kind:              PhysicalityType.Content,
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

    /* ============================================================
     * Static composition helpers — TextDecomposer + HashComposer +
     * (optionally) entity-row build. Single source of the resolver
     * + decomposition glue used by every text-bearing decomposer.
     * CodepointPerfcache.Load MUST be called by the host first;
     * the resolver returns -1 atom-not-found if records are absent.
     * ============================================================ */

    /// <summary>Perfcache-backed atom resolver for HashComposer.
    /// Exposed as an unmanaged function pointer; zero per-call allocation.</summary>
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

    /// <summary>
    /// Decompose canonical bytes through TextDecomposer + HashComposer and
    /// return the content-addressed root node (id / tier / 4D coord). Returns
    /// false if TextDecomposer rejects the input (e.g. invalid UTF-8).
    /// </summary>
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
            rootId = default; rootTier = 0;
            cx = cy = cz = cm = double.NaN;
            return false;
        }
    }

    /// <summary>
    /// Decompose + emit entity and CONTENT-physicality rows for the root and
    /// every tier-tree ancestor. Returns false if TextDecomposer rejects.
    /// </summary>
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
            entities = ImmutableArray<EntityRow>.Empty;
            physicalities = ImmutableArray<PhysicalityRow>.Empty;
            rootId = default; rootTier = 0;
            return false;
        }
    }

    /// <summary>
    /// Decompose + emit content (entities + CONTENT physicalities) AND the
    /// honest DISTRIBUTIONAL witness: per sentence, the adjacent word bigrams
    /// (<c>a PRECEDES b</c> / <c>b FOLLOWS a</c>), aggregated across the whole
    /// text so identical bigrams FOLD onto one evidence row (context NULL,
    /// observation_count = occurrences) — the §10/architecture position-fold.
    /// Each occurrence is a confirm (score 1); witness weight is the caller's
    /// (kind_rank × source_trust). Prose attests sequence/usage, NEVER meaning;
    /// the semantic arenas come from curated sources and models. Returns false
    /// if TextDecomposer rejects the input.
    /// </summary>
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
            entities = ImmutableArray<EntityRow>.Empty;
            physicalities = ImmutableArray<PhysicalityRow>.Empty;
            attestations = ImmutableArray<AttestationRow>.Empty;
            rootId = default; rootTier = 0;
            return false;
        }
    }

    /// <summary>
    /// The distributional witness of a composed text: adjacent within-sentence
    /// word bigrams, aggregated. Positions FOLD — every occurrence of the
    /// ordered pair (a,b) collapses to ONE evidence row with games = count and
    /// an exact Σscore (count × 1.0, each occurrence a confirm). Emits BOTH
    /// directed views per pair so either walk direction is an indexed
    /// per-subject read: <c>a PRECEDES b</c> (forward) and <c>b FOLLOWS a</c>
    /// (backward). Bounded at 2·(words−1) rows per sentence before the fold;
    /// the fold makes it the count of DISTINCT bigrams. Cross-sentence pairs
    /// are never formed — the sentence is the natural co-occurrence unit.
    /// </summary>
    public static ImmutableArray<AttestationRow> BuildDistributionalAttestations(
        TierTree tree, Hash128 sourceId, double witnessWeight)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var precedes = new Dictionary<(Hash128 A, Hash128 B), long>();
        var contentMemo = new Dictionary<Hash128, bool>();   // word id → has alnum content
        var content = new List<Hash128>();
        int n = tree.NodeCount;
        for (uint idx = 0; idx < (uint)n; idx++)
        {
            var node = tree.GetNode(idx);
            if (node.Tier != SentenceTier || node.ChildCount < 2) continue;

            // Adjacency is between CONTENT words: whitespace/punctuation tokens are
            // the mechanical delimiters, not content, and (being ultra-frequent
            // hubs) would drown the signal — "Captain<space>Ahab" must read as the
            // bigram (Captain, Ahab). Skip any token with no alphanumeric codepoint.
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

        var rows = ImmutableArray.CreateBuilder<AttestationRow>(precedes.Count);
        foreach (var (pair, count) in precedes)
        {
            // Each occurrence is a confirm (score 1.0 ⇒ 1e9 fixed-point).
            long sumScore = checked(count * Glicko2.FpScale);
            rows.Add(AttestationFactory.CreateAggregated(
                pair.A, PrecedesTypeId, pair.B, sourceId, contextId: null,
                games: count, sumScoreFp1e9: sumScore, witnessWeight: witnessWeight));
        }
        return rows.ToImmutable();
    }

    /// <summary>A token is CONTENT (vs a whitespace/punctuation delimiter) iff at
    /// least one of its codepoint leaves is a letter or digit. Memoized by the
    /// token's content id — repeated words ("the", "whale") resolve once.</summary>
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
