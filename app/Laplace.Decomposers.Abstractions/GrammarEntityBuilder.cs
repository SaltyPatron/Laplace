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
        int n = _ast.NodeCount;
        if (_utf8.Length == 0 || n == 0)
            return (ImmutableArray<EntityRow>.Empty, ImmutableArray<PhysicalityRow>.Empty,
                    ImmutableArray<AttestationRow>.Empty, default);

        IntPtr composeResult = IntPtr.Zero;
        fixed (byte* p = _utf8)
        {
            int rc = NativeInterop.GrammarCompose(
                p, (nuint)_utf8.Length, _ast.Handle, _modalityId,
                _sourceId, BootstrapIntentBuilder.TypeMetaTypeId, &composeResult);
            if (rc != 0 || composeResult == IntPtr.Zero)
                throw new InvalidOperationException($"laplace_grammar_compose returned {rc}");
        }

        try
        {
            long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            var entities      = ImmutableArray.CreateBuilder<EntityRow>();
            var physicalities = ImmutableArray.CreateBuilder<PhysicalityRow>();

            nuint nEnt = NativeInterop.ComposeEntityCount(composeResult);
            for (nuint i = 0; i < nEnt; i++)
            {
                NativeInterop.ComposeEntityNative e;
                NativeInterop.ComposeGetEntity(composeResult, i, &e);
                entities.Add(new EntityRow(e.Id, e.Tier, e.TypeId, _sourceId));
            }

            nuint nPhys = NativeInterop.ComposePhysicalityCount(composeResult);
            for (nuint i = 0; i < nPhys; i++)
            {
                NativeInterop.ComposePhysicalityNative ph;
                NativeInterop.ComposeGetPhysicality(composeResult, i, &ph);
                int trajLen = (int)ph.TrajectoryN.ToUInt64();
                double[] traj = trajLen > 0
                    ? new ReadOnlySpan<double>(ph.TrajectoryXyzm.ToPointer(), trajLen).ToArray()
                    : [];
                physicalities.Add(new PhysicalityRow(
                    Id: ph.Id, EntityId: ph.EntityId, SourceId: ph.SourceId,
                    Type: PhysicalityType.Content,
                    CoordX: ph.Coord0, CoordY: ph.Coord1, CoordZ: ph.Coord2, CoordM: ph.Coord3,
                    HilbertIndex: ph.Hilbert,
                    TrajectoryXyzm: traj,
                    NConstituents: (int)ph.NConstituents.ToUInt64(),
                    AlignmentResidual: null, SourceDim: null,
                    ObservedAtUnixUs: nowUs));
            }

            var attestations = ImmutableArray.CreateBuilder<AttestationRow>();
            nuint nPrec = NativeInterop.ComposePrecedesCount(composeResult);
            for (nuint i = 0; i < nPrec; i++)
            {
                NativeInterop.ComposePrecedesNative pr;
                NativeInterop.ComposeGetPrecedes(composeResult, i, &pr);
                long sumScore = checked(pr.Games * Glicko2.FpScale);
                attestations.Add(AttestationFactory.CreateAggregated(
                    pr.SubjectId, PrecedesTypeId, pr.ObjectId, _sourceId, contextId: null,
                    games: pr.Games, sumScoreFp1e9: sumScore, witnessWeight: witnessWeight));
            }

            var nodes = new LaplaceAstNode[n];
            var compId = new Hash128[n];
            var compValid = new bool[n];
            for (int i = 0; i < n; i++)
            {
                nodes[i] = _ast.GetNode(i);
                var nd = nodes[i];
                Hash128 spanId;
                if (NativeInterop.ComposeSpanLookup(
                        composeResult, nd.StartByte, nd.EndByte, &spanId) == 0)
                {
                    compId[i] = spanId;
                    compValid[i] = true;
                }
            }

            attestations.AddRange(BuildTagAttestations(nodes, compId, compValid, witnessWeight));
            return (entities.ToImmutable(), physicalities.ToImmutable(),
                    attestations.ToImmutable(), NativeInterop.ComposeRootId(composeResult));
        }
        finally
        {
            if (composeResult != IntPtr.Zero)
                NativeInterop.ComposeResultFree(composeResult);
        }
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
