using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;








public sealed class GrammarEntityBuilder
{
    public static readonly Hash128 PrecedesTypeId = RelationTypeRegistry.RelationTypeId("PRECEDES");

    private readonly byte[]     _utf8;
    private readonly GrammarAst _ast;
    private readonly Hash128    _sourceId;
    private readonly string     _modalityId;
    private readonly IntPtr     _recipe;    
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

    public static Hash128 GrammarNodeTypeId(string modalityId, string typeName) =>
        Hash128.OfCanonical($"substrate/type/grammar/{modalityId}/{typeName}/v1");

    
    
    
    
    
    public IReadOnlyCollection<string> NodeTypeCanonicalNames => _nodeTypeNames;
    private readonly HashSet<string> _nodeTypeNames = new(StringComparer.Ordinal);

    public (ImmutableArray<EntityRow> Entities,
            ImmutableArray<PhysicalityRow> Physicalities,
            ImmutableArray<AttestationRow> Attestations,
            Hash128 RootId) Build(double witnessWeight)
        => Build(witnessWeight, existingBitmap: null);

    private static readonly (ImmutableArray<EntityRow>,
                             ImmutableArray<PhysicalityRow>,
                             ImmutableArray<AttestationRow>, Hash128) Empty =
        (ImmutableArray<EntityRow>.Empty, ImmutableArray<PhysicalityRow>.Empty,
         ImmutableArray<AttestationRow>.Empty, default);

    
    
    
    
    
    
    public (ImmutableArray<EntityRow> Entities,
            ImmutableArray<PhysicalityRow> Physicalities,
            ImmutableArray<AttestationRow> Attestations,
            Hash128 RootId) Build(double witnessWeight, byte[]? existingBitmap)
    {
        if (_utf8.Length == 0 || _ast.NodeCount == 0) return Empty;
        IntPtr composeResult = Compose();
        try { return Extract(composeResult, witnessWeight, existingBitmap); }
        finally
        {
            if (composeResult != IntPtr.Zero)
                NativeInterop.ComposeResultFree(composeResult);
        }
    }

    /// <summary>
    /// Reader-driven tier-containment dedup, mirroring the structured/grammar ingest path: compose
    /// once, probe the composed entity ids via the existing <c>entities_exist_bitmap</c> facility,
    /// then emit only novel subtrees through <see cref="MerkleDedup.TrunkShortcircuit"/>. PRECEDES
    /// and tag attestations still flow (they carry new evidence).
    /// </summary>
    public async Task<(ImmutableArray<EntityRow> Entities,
                       ImmutableArray<PhysicalityRow> Physicalities,
                       ImmutableArray<AttestationRow> Attestations,
                       Hash128 RootId)> BuildAsync(
        double witnessWeight, ISubstrateReader? containmentReader, CancellationToken ct = default)
    {
        if (_utf8.Length == 0 || _ast.NodeCount == 0) return Empty;
        if (containmentReader is null) return Build(witnessWeight, null);

        IntPtr composeResult = Compose();
        try
        {
            byte[]? bitmap =
                await containmentReader.EntitiesExistBitmapAsync(ComposeEntityIds(composeResult), ct);
            return Extract(composeResult, witnessWeight, bitmap);
        }
        finally
        {
            if (composeResult != IntPtr.Zero)
                NativeInterop.ComposeResultFree(composeResult);
        }
    }

    private unsafe IntPtr Compose()
    {
        IntPtr composeResult = IntPtr.Zero;
        fixed (byte* p = _utf8)
        {
            int rc = NativeInterop.GrammarCompose(
                p, (nuint)_utf8.Length, _ast.Handle, _modalityId,
                _sourceId, BootstrapIntentBuilder.TypeMetaTypeId, &composeResult);
            if (rc != 0 || composeResult == IntPtr.Zero)
                throw new InvalidOperationException($"laplace_grammar_compose returned {rc}");
        }
        return composeResult;
    }

    private static unsafe Hash128[] ComposeEntityIds(IntPtr composeResult)
    {
        nuint nEnt = NativeInterop.ComposeEntityCount(composeResult);
        var ids = new Hash128[(int)nEnt];
        for (nuint i = 0; i < nEnt; i++)
        {
            NativeInterop.ComposeEntityNative e;
            NativeInterop.ComposeGetEntity(composeResult, i, &e);
            ids[(int)i] = e.Id;
        }
        return ids;
    }

    private unsafe (ImmutableArray<EntityRow> Entities,
                    ImmutableArray<PhysicalityRow> Physicalities,
                    ImmutableArray<AttestationRow> Attestations,
                    Hash128 RootId) Extract(
        IntPtr composeResult, double witnessWeight, byte[]? existingBitmap)
    {
        int n = _ast.NodeCount;
        {
            long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            var entities      = ImmutableArray.CreateBuilder<EntityRow>();
            var physicalities = ImmutableArray.CreateBuilder<PhysicalityRow>();

            
            
            bool[]? novelEntity = null;
            HashSet<Hash128>? novelIds = null;
            if (existingBitmap is { Length: > 0 })
            {
                IntPtr treePtr = NativeInterop.ComposeGetTierTree(composeResult);
                nuint nEnt0 = NativeInterop.ComposeEntityCount(composeResult);
                if (treePtr != IntPtr.Zero)
                {
                    using var tree = TierTree.FromBorrowedHandle(treePtr);
                    int nodeCount = tree.NodeCount;
                    if (nodeCount > 0 && nodeCount == (int)nEnt0
                        && existingBitmap.Length >= (nodeCount + 7) / 8)
                    {
                        var novelIdx = new uint[nodeCount];
                        int novelCount = MerkleDedup.TrunkShortcircuit(tree, existingBitmap, novelIdx);
                        novelEntity = new bool[nodeCount];
                        for (int i = 0; i < novelCount; i++) novelEntity[novelIdx[i]] = true;
                        novelIds = new HashSet<Hash128>(novelCount);
                        for (nuint i = 0; i < nEnt0; i++)
                        {
                            if (!novelEntity[(int)i]) continue;
                            NativeInterop.ComposeEntityNative e;
                            NativeInterop.ComposeGetEntity(composeResult, i, &e);
                            novelIds.Add(e.Id);
                        }
                    }
                }
            }

            nuint nEnt = NativeInterop.ComposeEntityCount(composeResult);
            for (nuint i = 0; i < nEnt; i++)
            {
                if (novelEntity is not null && !novelEntity[(int)i]) continue;
                NativeInterop.ComposeEntityNative e;
                NativeInterop.ComposeGetEntity(composeResult, i, &e);
                entities.Add(new EntityRow(e.Id, e.Tier, e.TypeId, _sourceId));
            }

            nuint nPhys = NativeInterop.ComposePhysicalityCount(composeResult);
            for (nuint i = 0; i < nPhys; i++)
            {
                NativeInterop.ComposePhysicalityNative ph;
                NativeInterop.ComposeGetPhysicality(composeResult, i, &ph);
                if (novelIds is not null && !novelIds.Contains(ph.EntityId)) continue;
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
                attestations.Add(NativeAttestation.Aggregated(
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
                if (_ast.NodeTypeName(nd.NodeTypeId) is { } typeName)
                    _nodeTypeNames.Add($"substrate/type/grammar/{_modalityId}/{typeName}/v1");
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
    }

    
    
    
    
    
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
            rows.Add(NativeAttestation.Aggregated(
                pair.A, PrecedesTypeId, pair.B, _sourceId, contextId: null,
                games: count, sumScoreFp1e9: sumScore, witnessWeight: witnessWeight));
        }
        return rows.ToImmutable();
    }

    
    
    
    
    
    
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
                switch (c.Type)
                {
                    case TagType.Name:        name    = id; break;
                    case TagType.DefFunction:
                    case TagType.DefType:
                    case TagType.DefVar:      def     = id; break;
                    case TagType.RefCall:     refCall = id; break;
                    case TagType.RefType:     refType = id; break;
                }
            }
            if (name is not { } nm) continue;
            if (def     is { } d)  rows.Add(NativeAttestation.Categorical(d,  "DEFINES",    nm, _sourceId, null, witnessWeight));
            if (refCall is { } rc) rows.Add(NativeAttestation.Categorical(rc, "CALLS",      nm, _sourceId, null, witnessWeight));
            if (refType is { } rt) rows.Add(NativeAttestation.Categorical(rt, "REFERENCES", nm, _sourceId, null, witnessWeight));
        }
        return rows.ToImmutable();
    }
}
