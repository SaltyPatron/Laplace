using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;


public sealed unsafe class GrammarRowComposer : IDisposable
{
    /// <inheritdoc cref="LaplaceCoreGate.Native"/>
    internal static object NativeComposeGate => LaplaceCoreGate.Native;

    private readonly byte[] _utf8;
    private readonly string _modalityId;
    private readonly Hash128 _sourceId;
    private readonly GrammarAst _ast;
    private IntPtr _compose;
    private IntPtr _probe;
    private bool _disposed;

    public GrammarRowComposer(byte[] utf8, GrammarAst ast, Hash128 sourceId, string modalityId)
    {
        _utf8 = utf8;
        _ast = ast;
        _sourceId = sourceId;
        _modalityId = modalityId;
    }

    public Task<byte[]?> ProbeDescentBitmapAsync(ISubstrateReader reader, CancellationToken ct = default)
        => GrammarRowComposerProbe.ProbeDescentBitmapAsync(this, reader, ct);

    public static bool TryProbeRowRoot(
        ReadOnlySpan<byte> utf8, GrammarAst ast, string modalityId, out Hash128 rootId, out byte tier)
    {
        rootId = default;
        tier = 0;
        byte tierLocal = 0;
        Hash128 rootLocal = default;
        fixed (byte* p = utf8)
        {
            lock (NativeComposeGate)
            {
                int rc = NativeInterop.GrammarComposeRowRoot(
                    p, (nuint)utf8.Length, ast.Handle, modalityId, &rootLocal, &tierLocal);
                if (rc != 0) return false;
            }
            rootId = rootLocal;
            tier = tierLocal;
            return true;
        }
    }

    internal void EnsureProbed()
    {
        if (_probe != IntPtr.Zero || _compose != IntPtr.Zero) return;
        lock (NativeComposeGate)
        {
            if (_probe != IntPtr.Zero || _compose != IntPtr.Zero) return;
            IntPtr result = IntPtr.Zero;
            fixed (byte* p = _utf8)
            {
                int rc = NativeInterop.GrammarComposeProbe(
                    p, (nuint)_utf8.Length, _ast.Handle, _modalityId,
                    _sourceId, BootstrapIntentBuilder.TypeMetaTypeId, &result);
                if (rc != 0 || result == IntPtr.Zero)
                    throw new InvalidOperationException($"laplace_grammar_compose_probe returned {rc}");
            }
            _probe = result;
        }
    }

    private void EnsureComposed()
    {
        EnsureComposed(existingBitmap: null);
    }

    private void EnsureComposed(byte[]? existingBitmap)
    {
        if (_compose != IntPtr.Zero) return;
        lock (NativeComposeGate)
        {
            if (_compose != IntPtr.Zero) return;
            if (_probe != IntPtr.Zero)
            {
                if (IsEntireTreePresent(existingBitmap))
                    return;
                fixed (byte* p = _utf8)
                {
                    int rc = NativeInterop.GrammarComposeMaterializePhys(
                        _probe, p, (nuint)_utf8.Length, _ast.Handle, _modalityId);
                    if (rc != 0)
                        throw new InvalidOperationException(
                            $"laplace_grammar_compose_materialize_phys returned {rc}");
                }
                _compose = _probe;
                return;
            }
            IntPtr result = IntPtr.Zero;
            fixed (byte* p = _utf8)
            {
                int rc = NativeInterop.GrammarCompose(
                    p, (nuint)_utf8.Length, _ast.Handle, _modalityId,
                    _sourceId, BootstrapIntentBuilder.TypeMetaTypeId, &result);
                if (rc != 0 || result == IntPtr.Zero)
                    throw new InvalidOperationException($"laplace_grammar_compose returned {rc}");
            }
            _compose = result;
        }
    }

    /// <summary>
    /// When the descent bitmap marks every compose-tree node present, skip
    /// <c>materialize_phys</c> and entity/physicality drain — PRECEDES + witness only.
    /// </summary>
    internal static bool IsEntireTreePresent(byte[]? existingBitmap, IntPtr composeOrProbe)
    {
        if (existingBitmap is not { Length: > 0 } || composeOrProbe == IntPtr.Zero)
            return false;
        IntPtr treePtr = NativeInterop.ComposeGetTierTree(composeOrProbe);
        if (treePtr == IntPtr.Zero) return false;
        using var tree = TierTree.FromBorrowedHandle(treePtr);
        int nodeCount = tree.NodeCount;
        nuint nEnt = NativeInterop.ComposeEntityCount(composeOrProbe);
        if (nodeCount == 0 || nodeCount != (int)nEnt) return false;
        if (existingBitmap.Length < (nodeCount + 7) / 8) return false;
        var novelIdx = new uint[nodeCount];
        return MerkleDedup.TrunkShortcircuit(tree, existingBitmap, novelIdx) == 0;
    }

    private bool IsEntireTreePresent(byte[]? existingBitmap)
    {
        EnsureProbed();
        return IsEntireTreePresent(existingBitmap, ActiveResult);
    }

    internal IntPtr BorrowedTierTree()
    {
        EnsureProbed();
        return NativeInterop.ComposeGetTierTree(ActiveResult);
    }

    private IntPtr ActiveResult => _compose != IntPtr.Zero ? _compose : _probe;

    
    
    
    
    
    
    
    
    
    
    public Hash128[] EntityIds()
    {
        EnsureProbed();
        nuint nEnt = NativeInterop.ComposeEntityCount(ActiveResult);
        var ids = new Hash128[(int)nEnt];
        for (nuint i = 0; i < nEnt; i++)
        {
            NativeInterop.ComposeEntityNative e;
            NativeInterop.ComposeGetEntity(ActiveResult, i, &e);
            ids[(int)i] = e.Id;
        }
        return ids;
    }

    
    private readonly struct EmitFilter
    {
        private readonly bool[]? _novelEntity;
        private readonly HashSet<Hash128>? _novelIds;
        public EmitFilter(bool[] novelEntity, HashSet<Hash128> novelIds)
        {
            _novelEntity = novelEntity;
            _novelIds = novelIds;
        }
        public bool EmitAll => _novelEntity is null;
        public bool EntityNovel(nuint i) => _novelEntity is null || _novelEntity[(int)i];
        public bool PhysNovel(Hash128 entityId) => _novelIds is null || _novelIds.Contains(entityId);
    }

    
    
    
    
    private EmitFilter ComputeFilter(byte[]? existingBitmap)
    {
        EnsureComposed(existingBitmap);
        if (existingBitmap is not { Length: > 0 }) return default;
        IntPtr treePtr = NativeInterop.ComposeGetTierTree(ActiveResult);
        if (treePtr == IntPtr.Zero) return default;   
        using var tree = TierTree.FromBorrowedHandle(treePtr);
        int nodeCount = tree.NodeCount;
        nuint nEnt = NativeInterop.ComposeEntityCount(ActiveResult);
        if (nodeCount == 0 || nodeCount != (int)nEnt) return default;
        if (existingBitmap.Length < (nodeCount + 7) / 8) return default;

        var novelIdx = new uint[nodeCount];
        int novelCount = MerkleDedup.TrunkShortcircuit(tree, existingBitmap, novelIdx);
        var novelEntity = new bool[nodeCount];
        for (int i = 0; i < novelCount; i++) novelEntity[novelIdx[i]] = true;
        var novelIds = new HashSet<Hash128>(novelCount);
        for (nuint i = 0; i < nEnt; i++)
        {
            if (!novelEntity[(int)i]) continue;
            NativeInterop.ComposeEntityNative e;
            NativeInterop.ComposeGetEntity(ActiveResult, i, &e);
            novelIds.Add(e.Id);
        }
        return new EmitFilter(novelEntity, novelIds);
    }

    public (ImmutableArray<EntityRow> Entities,
            ImmutableArray<PhysicalityRow> Physicalities,
            ImmutableArray<AttestationRow> Precedes,
            Hash128 RootId) Materialize(double witnessWeight)
        => Materialize(witnessWeight, existingBitmap: null);

    
    
    
    public (ImmutableArray<EntityRow> Entities,
            ImmutableArray<PhysicalityRow> Physicalities,
            ImmutableArray<AttestationRow> Precedes,
            Hash128 RootId) Materialize(double witnessWeight, byte[]? existingBitmap)
    {
        EnsureComposed(existingBitmap);
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var entities = ImmutableArray.CreateBuilder<EntityRow>();
        var physicalities = ImmutableArray.CreateBuilder<PhysicalityRow>();
        var precedes = ImmutableArray.CreateBuilder<AttestationRow>();
        var filter = ComputeFilter(existingBitmap);

        nuint nEnt = NativeInterop.ComposeEntityCount(ActiveResult);
        for (nuint i = 0; i < nEnt; i++)
        {
            if (!filter.EntityNovel(i)) continue;
            NativeInterop.ComposeEntityNative e;
            NativeInterop.ComposeGetEntity(ActiveResult, i, &e);
            entities.Add(new EntityRow(e.Id, e.Tier, e.TypeId, _sourceId));
        }

        nuint nPhys = NativeInterop.ComposePhysicalityCount(ActiveResult);
        for (nuint i = 0; i < nPhys; i++)
        {
            NativeInterop.ComposePhysicalityNative ph;
            NativeInterop.ComposeGetPhysicality(ActiveResult, i, &ph);
            if (!filter.PhysNovel(ph.EntityId)) continue;
            int trajLen = (int)ph.TrajectoryN.ToUInt64();
            double[] traj = trajLen > 0
                ? new ReadOnlySpan<double>(ph.TrajectoryXyzm.ToPointer(), trajLen).ToArray()
                : [];
            physicalities.Add(new PhysicalityRow(
                Id: ph.Id, EntityId: ph.EntityId, SourceId: _sourceId,
                Type: PhysicalityType.Content,
                CoordX: ph.Coord0, CoordY: ph.Coord1, CoordZ: ph.Coord2, CoordM: ph.Coord3,
                HilbertIndex: ph.Hilbert, TrajectoryXyzm: traj,
                NConstituents: (int)ph.NConstituents.ToUInt64(),
                AlignmentResidual: null, SourceDim: null, ObservedAtUnixUs: nowUs));
        }

        nuint nPrec = NativeInterop.ComposePrecedesCount(ActiveResult);
        for (nuint i = 0; i < nPrec; i++)
        {
            NativeInterop.ComposePrecedesNative pr;
            NativeInterop.ComposeGetPrecedes(ActiveResult, i, &pr);
            long sumScore = checked(pr.Games * Glicko2.FpScale);
            precedes.Add(NativeAttestation.Aggregated(
                pr.SubjectId, GrammarEntityBuilder.PrecedesTypeId, pr.ObjectId,
                _sourceId, contextId: null,
                games: pr.Games, sumScoreFp1e9: sumScore, witnessWeight: witnessWeight));
        }

        return (entities.ToImmutable(), physicalities.ToImmutable(),
                precedes.ToImmutable(), NativeInterop.ComposeRootId(ActiveResult));
    }

    /// <summary>
    /// Drains the native compose result straight into <paramref name="stage"/> (entities +
    /// physicalities) with no managed <c>EntityRow</c>/<c>PhysicalityRow</c> marshal — the stage's
    /// native tuple buffers stream directly to COPY. This is the same one-hop path the
    /// content-witness composer already uses; the grammar composer just emitted managed rows
    /// instead. PRECEDES bigrams carry Glicko signal the native attestation row can't hold, so
    /// they are appended to <paramref name="precedesOut"/> as managed <c>AttestationRow</c>s (low
    /// volume) for the existing consensus path, until the walk-journal fold subsumes them.
    /// <paramref name="nowUs"/> is threaded in (not read from the clock) so output is reproducible.
    /// Returns the compose root id. Byte-for-byte identical entity/physicality blobs to
    /// <see cref="Materialize"/>+re-stage for the same <paramref name="nowUs"/>.
    /// </summary>
    public Hash128 DrainInto(
        IntentStage stage, double witnessWeight, long nowUs,
        ImmutableArray<AttestationRow>.Builder precedesOut)
        => DrainInto(stage, witnessWeight, nowUs, precedesOut, existingBitmap: null);

    
    
    
    public Hash128 DrainInto(
        IntentStage stage, double witnessWeight, long nowUs,
        ImmutableArray<AttestationRow>.Builder precedesOut, byte[]? existingBitmap)
    {
        EnsureComposed(existingBitmap);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(precedesOut);
        var filter = ComputeFilter(existingBitmap);

        nuint nEnt = NativeInterop.ComposeEntityCount(ActiveResult);
        for (nuint i = 0; i < nEnt; i++)
        {
            if (!filter.EntityNovel(i)) continue;
            NativeInterop.ComposeEntityNative e;
            NativeInterop.ComposeGetEntity(ActiveResult, i, &e);
            stage.AddEntity(e.Id, e.Tier, e.TypeId, _sourceId);
        }

        nuint nPhys = NativeInterop.ComposePhysicalityCount(_compose);
        Span<double> coord = stackalloc double[4];
        for (nuint i = 0; i < nPhys; i++)
        {
            NativeInterop.ComposePhysicalityNative ph;
            NativeInterop.ComposeGetPhysicality(_compose, i, &ph);
            if (!filter.PhysNovel(ph.EntityId)) continue;
            coord[0] = ph.Coord0; coord[1] = ph.Coord1; coord[2] = ph.Coord2; coord[3] = ph.Coord3;
            int trajLen = (int)ph.TrajectoryN.ToUInt64();
            var traj = trajLen > 0
                ? new ReadOnlySpan<double>(ph.TrajectoryXyzm.ToPointer(), trajLen)
                : ReadOnlySpan<double>.Empty;
            stage.AddPhysicality(
                ph.Id, ph.EntityId, (short)PhysicalityType.Content,
                coord, ph.Hilbert, traj, (int)ph.NConstituents.ToUInt64(),
                alignmentResidual: null, sourceDim: null, observedAtUnixUs: nowUs);
        }

        nuint nPrec = NativeInterop.ComposePrecedesCount(ActiveResult);
        for (nuint i = 0; i < nPrec; i++)
        {
            NativeInterop.ComposePrecedesNative pr;
            NativeInterop.ComposeGetPrecedes(ActiveResult, i, &pr);
            long sumScore = checked(pr.Games * Glicko2.FpScale);
            precedesOut.Add(NativeAttestation.Aggregated(
                pr.SubjectId, GrammarEntityBuilder.PrecedesTypeId, pr.ObjectId,
                _sourceId, contextId: null,
                games: pr.Games, sumScoreFp1e9: sumScore, witnessWeight: witnessWeight));
        }

        return NativeInterop.ComposeRootId(ActiveResult);
    }

    /// <summary>
    /// Live drain: writes the compose entities + physicalities straight into
    /// <see cref="SubstrateChangeBuilder.ContentStage"/> (native, no managed row marshal), deduped
    /// within the batch via the builder's shared seen-set (so an id also emitted by a witness via
    /// <see cref="SubstrateChangeBuilder.AddEntity(EntityRow)"/> is staged once). PRECEDES bigrams
    /// ride as managed attestations through the builder. Returns the compose root id.
    /// </summary>
    public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight)
        => DrainInto(builder, witnessWeight, existingBitmap: null);

    
    
    
    public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? existingBitmap)
    {
        EnsureComposed(existingBitmap);
        ArgumentNullException.ThrowIfNull(builder);
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var stage = builder.ContentStage;

        unsafe
        {
            Hash128 src = _sourceId;
            IntPtr active = ActiveResult;
            lock (NativeComposeGate)
            {
                if (existingBitmap is { Length: > 0 })
                {
                    fixed (byte* bm = existingBitmap)
                    {
                        int rc = NativeInterop.ComposeDrainIntoStage(
                            active, stage.DangerousNativeHandle, &src, nowUs, witnessWeight,
                            bm, (nuint)existingBitmap.Length * 8);
                        if (rc != 0)
                            throw new InvalidOperationException($"laplace_compose_drain_into_stage returned {rc}");
                    }
                }
                else
                {
                    int rc = NativeInterop.ComposeDrainIntoStage(
                        active, stage.DangerousNativeHandle, &src, nowUs, witnessWeight, null, 0);
                    if (rc != 0)
                        throw new InvalidOperationException($"laplace_compose_drain_into_stage returned {rc}");
                }
            }
        }

        // Keep the builder's within-batch seen-set in sync with what the native drain staged.
        nuint nEnt = NativeInterop.ComposeEntityCount(ActiveResult);
        for (nuint i = 0; i < nEnt; i++)
        {
            NativeInterop.ComposeEntityNative e;
            NativeInterop.ComposeGetEntity(ActiveResult, i, &e);
            builder.TrySeeEntity(e.Id);
        }
        nuint nPhys = NativeInterop.ComposePhysicalityCount(ActiveResult);
        for (nuint i = 0; i < nPhys; i++)
        {
            NativeInterop.ComposePhysicalityNative ph;
            NativeInterop.ComposeGetPhysicality(ActiveResult, i, &ph);
            builder.TrySeePhysicality(ph.Id);
        }

        return NativeInterop.ComposeRootId(ActiveResult);
    }

    public bool TrySpanEntity(uint startByte, uint endByte, out Hash128 id)
    {
        EnsureComposed();
        id = default;
        fixed (Hash128* p = &id)
            return NativeInterop.ComposeSpanLookup(ActiveResult, startByte, endByte, p) == 0;
    }

    public IReadOnlyList<(uint Start, uint End)> FieldSpans()
    {
        var list = new List<(uint, uint)>();
        for (int i = 0; i < _ast.NodeCount; i++)
        {
            var nd = _ast.GetNode(i);
            if (_ast.NodeTypeName(nd.NodeTypeId) == "field")
                list.Add((nd.StartByte, nd.EndByte));
        }
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (NativeComposeGate)
        {
            if (_compose != IntPtr.Zero && _compose != _probe)
                NativeInterop.ComposeResultFree(_compose);
            if (_probe != IntPtr.Zero)
                NativeInterop.ComposeResultFree(_probe);
        }
        _compose = IntPtr.Zero;
        _probe = IntPtr.Zero;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
