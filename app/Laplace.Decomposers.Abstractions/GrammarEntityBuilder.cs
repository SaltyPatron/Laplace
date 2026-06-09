using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Composes a parsed <see cref="GrammarAst"/> into substrate rows — the grammar analog of
/// <see cref="TextEntityBuilder"/>. Both share the codepoint→grapheme floor (so a code identifier
/// and a prose word reconcile by id) and the one composition kernel (<c>HashComposer.ComposeNode</c>).
/// Constituency is the content trajectory; tree-sitter's node-kinds become first-class substrate
/// type entities (<c>substrate/type/grammar/{modality}/{kind}/v1</c>) — no foreign type persists.
/// </summary>
public sealed class GrammarEntityBuilder
{
    public static readonly Hash128 PrecedesTypeId = RelationTypeRegistry.RelationTypeId("PRECEDES");

    private readonly byte[]     _utf8;
    private readonly GrammarAst _ast;
    private readonly Hash128    _sourceId;
    private readonly string     _modalityId;
    private readonly IntPtr     _recipe;    // for tags.scm semantic arcs (optional)
    private readonly byte[]?    _tagsScm;

    public GrammarEntityBuilder(byte[] utf8, GrammarAst ast, Hash128 sourceId, string modalityId,
                                IntPtr recipe = default, byte[]? tagsScm = null)
    {
        _utf8       = utf8 ?? throw new ArgumentNullException(nameof(utf8));
        _ast        = ast ?? throw new ArgumentNullException(nameof(ast));
        _sourceId   = sourceId;
        _modalityId = modalityId ?? throw new ArgumentNullException(nameof(modalityId));
        _recipe     = recipe;
        _tagsScm    = tagsScm;
    }

    public static Hash128 KindTypeId(string modalityId, string kindName) =>
        Hash128.OfCanonical($"substrate/type/grammar/{modalityId}/{kindName}/v1");

    public unsafe (ImmutableArray<EntityRow> Entities,
                   ImmutableArray<PhysicalityRow> Physicalities,
                   ImmutableArray<AttestationRow> Attestations,
                   Hash128 RootId) Build(double witnessWeight)
    {
        var entities      = ImmutableArray.CreateBuilder<EntityRow>();
        var physicalities = ImmutableArray.CreateBuilder<PhysicalityRow>();

        int n = _ast.NodeCount;
        if (_utf8.Length == 0 || n == 0)
            return (entities.ToImmutable(), physicalities.ToImmutable(),
                    ImmutableArray<AttestationRow>.Empty, default);

        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

        // 1. shared grapheme floor; resolve codepoint/grapheme ids+coords from the perfcache
        //    (the same resolver text uses).
        using var floor = GraphemeFloor.Build(_utf8);
        HashComposer.Run(floor.Tree, &TextEntityBuilder.Resolver);

        // 1a. emit grapheme entities/physicalities (TextEntityBuilder over the floor-only tree
        //     emits tier-1 graphemes and skips tier-0 codepoints).
        var (gEnts, gPhys) = new TextEntityBuilder(floor.Tree, _sourceId).Build();
        entities.AddRange(gEnts);
        physicalities.AddRange(gPhys);

        // 2. AST nodes + childrenOf adjacency (named children, source order; one O(n) pass).
        var nodes      = new LaplaceAstNode[n];
        var childrenOf = new List<int>?[n];
        for (int i = 0; i < n; i++)
        {
            nodes[i] = _ast.GetNode(i);
            uint parent = nodes[i].Parent;
            if (parent != GrammarAst.Root && parent < (uint)n)
                (childrenOf[(int)parent] ??= new List<int>()).Add(i);
        }

        // 3. bottom-up composition (reverse pre-order index = children before parents).
        var compId    = new Hash128[n];
        var compCoord = new double[n * 4];
        var compTier  = new byte[n];
        var compValid = new bool[n];
        var emittedKindTypes = new HashSet<Hash128>();
        var emittedAstIds    = new HashSet<Hash128>();
        var outCoord = new double[4];

        for (int idx = n - 1; idx >= 0; idx--)
        {
            var node = nodes[idx];
            var kids = childrenOf[idx];

            Hash128[] childIds;
            double[]  childCoords;
            ulong[]   childFlags;
            byte      tier;

            if (kids is { Count: > 0 })
            {
                // interior node: constituents are the composed child AST nodes.
                int m = kids.Count;
                childIds    = new Hash128[m];
                childCoords = new double[m * 4];
                childFlags  = new ulong[m];
                byte maxTier = 0;
                int w = 0;
                for (int j = 0; j < m; j++)
                {
                    int c = kids[j];
                    if (!compValid[c]) continue;
                    childIds[w] = compId[c];
                    Array.Copy(compCoord, c * 4, childCoords, w * 4, 4);
                    byte ct = compTier[c];
                    childFlags[w] = Trajectory.VertexFlags(ct, hasAtom: false, atom: 0);
                    if (ct > maxTier) maxTier = ct;
                    w++;
                }
                if (w == 0) { compValid[idx] = false; continue; }
                if (w != m)
                {
                    Array.Resize(ref childIds, w);
                    Array.Resize(ref childCoords, w * 4);
                    Array.Resize(ref childFlags, w);
                }
                tier = (byte)Math.Min(255, maxTier + 1);
            }
            else
            {
                // terminal node: constituents are the graphemes of its byte span.
                if (!floor.SpanToGraphemes(node.StartByte, node.EndByte, out int gStart, out int gEnd))
                { compValid[idx] = false; continue; }
                int m = gEnd - gStart;
                childIds    = new Hash128[m];
                childCoords = new double[m * 4];
                childFlags  = new ulong[m];
                for (int g = gStart; g < gEnd; g++)
                {
                    var gv = floor.Tree.GetNode((uint)floor.GraphemeNodeIndex(g));
                    int j = g - gStart;
                    childIds[j] = gv.Id;
                    childCoords[j * 4 + 0] = gv.Coord[0];
                    childCoords[j * 4 + 1] = gv.Coord[1];
                    childCoords[j * 4 + 2] = gv.Coord[2];
                    childCoords[j * 4 + 3] = gv.Coord[3];
                    childFlags[j] = Trajectory.VertexFlags(1, hasAtom: false, atom: 0);  // grapheme tier
                }
                tier = 2;  // terminal sits at the word tier → reconciles with prose words
            }

            var (id, hb) = HashComposer.ComposeNode(tier, childIds, childCoords, outCoord);
            compId[idx] = id;
            compCoord[idx * 4 + 0] = outCoord[0];
            compCoord[idx * 4 + 1] = outCoord[1];
            compCoord[idx * 4 + 2] = outCoord[2];
            compCoord[idx * 4 + 3] = outCoord[3];
            compTier[idx] = tier;
            compValid[idx] = true;

            if (!emittedAstIds.Add(id)) continue;  // dedup within this file

            var kindTypeId = KindTypeId(_modalityId, _ast.KindName(node.KindId) ?? "unknown");
            if (emittedKindTypes.Add(kindTypeId))
                entities.Add(new EntityRow(kindTypeId, EntityTier.Vocabulary,
                                           BootstrapIntentBuilder.TypeMetaTypeId, _sourceId));

            entities.Add(new EntityRow(id, tier, kindTypeId, _sourceId));

            var traj = Trajectory.Build(childIds, childFlags);
            var physId = PhysicalityId.Compute(id, _sourceId, PhysicalityType.Content,
                                               outCoord[0], outCoord[1], outCoord[2], outCoord[3], traj);
            physicalities.Add(new PhysicalityRow(
                Id:                physId,
                EntityId:          id,
                SourceId:          _sourceId,
                Type:              PhysicalityType.Content,
                CoordX:            outCoord[0],
                CoordY:            outCoord[1],
                CoordZ:            outCoord[2],
                CoordM:            outCoord[3],
                HilbertIndex:      hb,
                TrajectoryXyzm:    traj,
                NConstituents:     childIds.Length,
                AlignmentResidual: null,
                SourceDim:         null,
                ObservedAtUnixUs:  nowUs));
        }

        var rootId = compValid[0] ? compId[0] : default;
        var attestations = BuildSequenceAttestations(childrenOf, compId, compValid, witnessWeight)
            .AddRange(BuildTagAttestations(nodes, compId, compValid, witnessWeight));
        return (entities.ToImmutable(), physicalities.ToImmutable(), attestations, rootId);
    }

    /// <summary>
    /// PRECEDES over adjacent named children of each composite node — the grammar analog of
    /// <see cref="TextEntityBuilder.BuildDistributionalAttestations"/>; feeds the existing
    /// collocates/generate consensus surface with zero new SQL.
    /// </summary>
    private ImmutableArray<AttestationRow> BuildSequenceAttestations(
        List<int>?[] childrenOf, Hash128[] compId, bool[] compValid, double witnessWeight)
    {
        var precedes = new Dictionary<(Hash128 A, Hash128 B), long>();
        for (int p = 0; p < childrenOf.Length; p++)
        {
            var kids = childrenOf[p];
            if (kids is null || kids.Count < 2) continue;
            for (int i = 1; i < kids.Count; i++)
            {
                int a = kids[i - 1], b = kids[i];
                if (!compValid[a] || !compValid[b]) continue;
                var key = (compId[a], compId[b]);
                precedes.TryGetValue(key, out long c);
                precedes[key] = c + 1;
            }
        }
        if (precedes.Count == 0) return ImmutableArray<AttestationRow>.Empty;

        var rows = ImmutableArray.CreateBuilder<AttestationRow>(precedes.Count);
        foreach (var (pair, count) in precedes)
        {
            long sumScore = checked(count * Glicko2.FpScale);
            rows.Add(AttestationFactory.CreateAggregated(
                pair.A, PrecedesTypeId, pair.B, _sourceId, contextId: null,
                games: count, sumScoreFp1e9: sumScore, witnessWeight: witnessWeight));
        }
        return rows.ToImmutable();
    }

    /// <summary>
    /// Typed semantic arcs from the grammar's tags.scm: one DEFINES / CALLS / REFERENCES edge per
    /// matched definition/reference. A capture's byte span correlates to the AST node (hence entity
    /// id) at that span, so a def or call resolves to real substrate entities. No-op when no recipe
    /// or tags.scm was supplied (structure then rides on PRECEDES alone).
    /// </summary>
    private ImmutableArray<AttestationRow> BuildTagAttestations(
        LaplaceAstNode[] nodes, Hash128[] compId, bool[] compValid, double witnessWeight)
    {
        if (_recipe == IntPtr.Zero || _tagsScm is null) return ImmutableArray<AttestationRow>.Empty;

        var caps = GrammarTags.Run(_recipe, _tagsScm, _utf8);
        if (caps.Count == 0) return ImmutableArray<AttestationRow>.Empty;

        var spanId = new Dictionary<(uint, uint), Hash128>();
        for (int i = 0; i < nodes.Length; i++)
            if (compValid[i]) spanId.TryAdd((nodes[i].StartByte, nodes[i].EndByte), compId[i]);

        var rows = ImmutableArray.CreateBuilder<AttestationRow>();
        foreach (var grp in caps.GroupBy(c => c.MatchId))
        {
            Hash128? name = null, def = null, refCall = null, refType = null;
            foreach (var c in grp)
            {
                if (!spanId.TryGetValue((c.StartByte, c.EndByte), out var id)) continue;
                switch (c.Kind)
                {
                    case TagKind.Name:        name    = id; break;
                    case TagKind.DefFunction:
                    case TagKind.DefType:
                    case TagKind.DefVar:      def     = id; break;
                    case TagKind.RefCall:     refCall = id; break;
                    case TagKind.RefType:     refType = id; break;
                }
            }
            if (name is not { } nm) continue;
            if (def     is { } d)  rows.Add(RelationTypeRegistry.Attest(d,  "DEFINES",    nm, _sourceId, witnessWeight));
            if (refCall is { } rc) rows.Add(RelationTypeRegistry.Attest(rc, "CALLS",      nm, _sourceId, witnessWeight));
            if (refType is { } rt) rows.Add(RelationTypeRegistry.Attest(rt, "REFERENCES", nm, _sourceId, witnessWeight));
        }
        return rows.ToImmutable();
    }
}
