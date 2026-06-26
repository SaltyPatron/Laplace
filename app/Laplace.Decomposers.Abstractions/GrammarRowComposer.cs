using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;


public sealed unsafe class GrammarRowComposer : IDisposable
{
    private readonly Hash128 _sourceId;
    private readonly GrammarAst _ast;
    private readonly IntPtr _compose;
    private bool _disposed;

    public GrammarRowComposer(byte[] utf8, GrammarAst ast, Hash128 sourceId, string modalityId)
    {
        _sourceId = sourceId;
        _ast = ast;
        IntPtr result = IntPtr.Zero;
        fixed (byte* p = utf8)
        {
            int rc = NativeInterop.GrammarCompose(
                p, (nuint)utf8.Length, ast.Handle, modalityId,
                sourceId, BootstrapIntentBuilder.TypeMetaTypeId, &result);
            if (rc != 0 || result == IntPtr.Zero)
                throw new InvalidOperationException($"laplace_grammar_compose returned {rc}");
        }
        _compose = result;
    }

    
    
    
    
    
    
    
    
    
    
    public Hash128[] EntityIds()
    {
        nuint nEnt = NativeInterop.ComposeEntityCount(_compose);
        var ids = new Hash128[(int)nEnt];
        for (nuint i = 0; i < nEnt; i++)
        {
            NativeInterop.ComposeEntityNative e;
            NativeInterop.ComposeGetEntity(_compose, i, &e);
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
        if (existingBitmap is not { Length: > 0 }) return default;
        IntPtr treePtr = NativeInterop.ComposeGetTierTree(_compose);
        if (treePtr == IntPtr.Zero) return default;   
        using var tree = TierTree.FromBorrowedHandle(treePtr);
        int nodeCount = tree.NodeCount;
        nuint nEnt = NativeInterop.ComposeEntityCount(_compose);
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
            NativeInterop.ComposeGetEntity(_compose, i, &e);
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
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var entities = ImmutableArray.CreateBuilder<EntityRow>();
        var physicalities = ImmutableArray.CreateBuilder<PhysicalityRow>();
        var precedes = ImmutableArray.CreateBuilder<AttestationRow>();
        var filter = ComputeFilter(existingBitmap);

        nuint nEnt = NativeInterop.ComposeEntityCount(_compose);
        for (nuint i = 0; i < nEnt; i++)
        {
            if (!filter.EntityNovel(i)) continue;
            NativeInterop.ComposeEntityNative e;
            NativeInterop.ComposeGetEntity(_compose, i, &e);
            entities.Add(new EntityRow(e.Id, e.Tier, e.TypeId, _sourceId));
        }

        nuint nPhys = NativeInterop.ComposePhysicalityCount(_compose);
        for (nuint i = 0; i < nPhys; i++)
        {
            NativeInterop.ComposePhysicalityNative ph;
            NativeInterop.ComposeGetPhysicality(_compose, i, &ph);
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

        nuint nPrec = NativeInterop.ComposePrecedesCount(_compose);
        for (nuint i = 0; i < nPrec; i++)
        {
            NativeInterop.ComposePrecedesNative pr;
            NativeInterop.ComposeGetPrecedes(_compose, i, &pr);
            long sumScore = checked(pr.Games * Glicko2.FpScale);
            precedes.Add(NativeAttestation.Aggregated(
                pr.SubjectId, GrammarEntityBuilder.PrecedesTypeId, pr.ObjectId,
                _sourceId, contextId: null,
                games: pr.Games, sumScoreFp1e9: sumScore, witnessWeight: witnessWeight));
        }

        return (entities.ToImmutable(), physicalities.ToImmutable(),
                precedes.ToImmutable(), NativeInterop.ComposeRootId(_compose));
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
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(precedesOut);
        var filter = ComputeFilter(existingBitmap);

        nuint nEnt = NativeInterop.ComposeEntityCount(_compose);
        for (nuint i = 0; i < nEnt; i++)
        {
            if (!filter.EntityNovel(i)) continue;
            NativeInterop.ComposeEntityNative e;
            NativeInterop.ComposeGetEntity(_compose, i, &e);
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

        nuint nPrec = NativeInterop.ComposePrecedesCount(_compose);
        for (nuint i = 0; i < nPrec; i++)
        {
            NativeInterop.ComposePrecedesNative pr;
            NativeInterop.ComposeGetPrecedes(_compose, i, &pr);
            long sumScore = checked(pr.Games * Glicko2.FpScale);
            precedesOut.Add(NativeAttestation.Aggregated(
                pr.SubjectId, GrammarEntityBuilder.PrecedesTypeId, pr.ObjectId,
                _sourceId, contextId: null,
                games: pr.Games, sumScoreFp1e9: sumScore, witnessWeight: witnessWeight));
        }

        return NativeInterop.ComposeRootId(_compose);
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
        ArgumentNullException.ThrowIfNull(builder);
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var stage = builder.ContentStage;

        unsafe
        {
            Hash128 src = _sourceId;
            if (existingBitmap is { Length: > 0 })
            {
                fixed (byte* bm = existingBitmap)
                {
                    int rc = NativeInterop.ComposeDrainIntoStage(
                        _compose, stage.DangerousNativeHandle, &src, nowUs, witnessWeight,
                        bm, (nuint)existingBitmap.Length * 8);
                    if (rc != 0)
                        throw new InvalidOperationException($"laplace_compose_drain_into_stage returned {rc}");
                }
            }
            else
            {
                int rc = NativeInterop.ComposeDrainIntoStage(
                    _compose, stage.DangerousNativeHandle, &src, nowUs, witnessWeight, null, 0);
                if (rc != 0)
                    throw new InvalidOperationException($"laplace_compose_drain_into_stage returned {rc}");
            }
        }

        // Keep the builder's within-batch seen-set in sync with what the native drain staged.
        nuint nEnt = NativeInterop.ComposeEntityCount(_compose);
        for (nuint i = 0; i < nEnt; i++)
        {
            NativeInterop.ComposeEntityNative e;
            NativeInterop.ComposeGetEntity(_compose, i, &e);
            builder.TrySeeEntity(e.Id);
        }
        nuint nPhys = NativeInterop.ComposePhysicalityCount(_compose);
        for (nuint i = 0; i < nPhys; i++)
        {
            NativeInterop.ComposePhysicalityNative ph;
            NativeInterop.ComposeGetPhysicality(_compose, i, &ph);
            builder.TrySeePhysicality(ph.Id);
        }

        return NativeInterop.ComposeRootId(_compose);
    }

    public bool TrySpanEntity(uint startByte, uint endByte, out Hash128 id)
    {
        id = default;
        fixed (Hash128* p = &id)
            return NativeInterop.ComposeSpanLookup(_compose, startByte, endByte, p) == 0;
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
        if (_compose != IntPtr.Zero)
            NativeInterop.ComposeResultFree(_compose);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
