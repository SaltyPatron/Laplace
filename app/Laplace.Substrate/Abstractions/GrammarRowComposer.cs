using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Decomposers.Abstractions;

public enum GrammarCompositionMode { CuratedRecord, FullSource }

public sealed unsafe class GrammarRowComposer : IDisposable
{

    internal static string DescribeContent(byte[] utf8)
    {
        int bad = -1;
        for (int i = 0; i < utf8.Length;)
        {
            byte b = utf8[i];
            int need = b < 0x80 ? 0 : (b & 0xE0) == 0xC0 ? 1 : (b & 0xF0) == 0xE0 ? 2
                     : (b & 0xF8) == 0xF0 ? 3 : -1;
            if (need < 0) { bad = i; break; }
            if (i + need >= utf8.Length && need > 0) { bad = i; break; }
            bool ok = true;
            for (int j = 1; j <= need; j++)
                if ((utf8[i + j] & 0xC0) != 0x80) { ok = false; break; }
            if (!ok) { bad = i; break; }
            i += need + 1;
        }
        string where = bad < 0 ? "utf8-valid" : $"first invalid byte at offset {bad}";
        string hex = "";
        if (bad >= 0)
        {
            int h0 = Math.Max(0, bad - 8), h1 = Math.Min(utf8.Length, bad + 8);
            hex = " bytes[" + h0 + ".." + h1 + ")=" + Convert.ToHexString(utf8, h0, h1 - h0);
            int p0 = Math.Max(0, bad - 80);
            string ctxText = System.Text.Encoding.UTF8.GetString(utf8, p0, bad - p0);
            hex += " preceding-text=\"" + ctxText.Replace('\n', ' ').Replace('\r', ' ') + "\"";
        }
        return $"len={utf8.Length}, {where}{hex}";
    }

    private readonly byte[] _utf8;
    private readonly string _modalityId;
    private readonly Hash128 _sourceId;
    private readonly GrammarAst _ast;
    private readonly GrammarCompositionMode _mode;
    private readonly object _sync = new();
    private IntPtr _compose;
    private IntPtr _probe;
    private bool _disposed;

    public GrammarRowComposer(byte[] utf8, GrammarAst ast, Hash128 sourceId, string modalityId,
                              GrammarCompositionMode mode = GrammarCompositionMode.CuratedRecord)
    {
        // Composition is also used by cold readback processes, before any ingestion.
        CodepointPerfcache.LoadDefault();
        _utf8 = utf8;
        _ast = ast;
        _sourceId = sourceId;
        _modalityId = modalityId;
        _mode = mode;
    }

    public Task<byte[]?> ProbeDescentBitmapAsync(ISubstrateReader reader, CancellationToken ct = default)
        => GrammarRowComposerProbe.ProbeDescentBitmapAsync(this, reader, ct);

    public static bool TryProbeRowRoot(
        ReadOnlySpan<byte> utf8, GrammarAst ast, string modalityId, out Hash128 rootId, out byte tier)
    {
        CodepointPerfcache.LoadDefault();
        rootId = default;
        tier = 0;
        byte tierLocal = 0;
        Hash128 rootLocal = default;
        fixed (byte* p = utf8)
        {
            int rc = NativeInterop.GrammarComposeRowRoot(
                p, (nuint)utf8.Length, ast.Handle, modalityId, &rootLocal, &tierLocal);
            if (rc != 0) return false;
            rootId = rootLocal;
            tier = tierLocal;
            return true;
        }
    }

    internal void EnsureProbed()
    {
        if (_probe != IntPtr.Zero || _compose != IntPtr.Zero) return;
        lock (_sync)
        {
            if (_probe != IntPtr.Zero || _compose != IntPtr.Zero) return;
            IntPtr result = IntPtr.Zero;
            fixed (byte* p = _utf8)
            {
                int rc = _mode == GrammarCompositionMode.FullSource
                    ? NativeInterop.GrammarSourceCompose(
                        p, (nuint)_utf8.Length, _ast.Handle, _modalityId, &result)
                    : NativeInterop.GrammarComposeProbe(
                        p, (nuint)_utf8.Length, _ast.Handle, _modalityId,
                        _sourceId, BootstrapIntentBuilder.TypeMetaTypeId, &result);
                if (rc != 0 || result == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"{(_mode == GrammarCompositionMode.FullSource ? "laplace_grammar_source_compose" : "laplace_grammar_compose_probe")} returned {rc} ({DescribeContent(_utf8)})");
            }
            if (_mode == GrammarCompositionMode.FullSource) _compose = result;
            else _probe = result;
        }
    }

    private void EnsureComposed()
    {
        EnsureComposed(existingBitmap: null);
    }

    private void EnsureComposed(byte[]? existingBitmap)
    {
        if (_mode == GrammarCompositionMode.FullSource)
        {
            EnsureProbed();
            return;
        }
        if (_compose != IntPtr.Zero) return;
        lock (_sync)
        {
            if (_compose != IntPtr.Zero) return;

            bool emitAll = existingBitmap is null || existingBitmap.Length == 0;
            // Entity membership never proves that this source unit's forms were
            // admitted. Materialize the native body even when every entity exists.
            if (_probe != IntPtr.Zero && !emitAll)
            {
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

            if (_probe != IntPtr.Zero)
            {
                NativeInterop.ComposeResultFree(_probe);
                _probe = IntPtr.Zero;
            }

            IntPtr result = IntPtr.Zero;
            fixed (byte* p = _utf8)
            {
                int rc = NativeInterop.GrammarCompose(
                    p, (nuint)_utf8.Length, _ast.Handle, _modalityId,
                    _sourceId, BootstrapIntentBuilder.TypeMetaTypeId, &result);
                if (rc != 0 || result == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"laplace_grammar_compose returned {rc} ({DescribeContent(_utf8)})");
            }
            _compose = result;
        }
    }

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

    public long ResidentBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return checked((long)NativeInterop.ComposeResidentBytes(_compose)
                + (long)NativeInterop.ComposeResidentBytes(_probe)
                + (long)NativeInterop.AstResidentBytes(_ast.Handle)
                + _utf8.LongLength);
        }
    }











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
        long grant = IngestSizing.ResolveWorkingSetBudgetBytes();
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        using var all = IntentStage.NewBounded(0, grant);
        Hash128 root = EmitNativeStage(all, witnessWeight, IngestClock.NowUnixUs(), null);
        var (entities, physicalities) = TextEntityBuilder.ExportNativeStage(all, _sourceId, grant);
        if (existingBitmap is { Length: > 0 })
        {
            // Materialize is an inspection snapshot: it retains every observed body.
            // Presence still filters entities through the same Native traversal used
            // by persistence. Production ingestion drains directly, without row arrays.
            long remaining = checked(grant - all.AllocatedBytes
                - (GC.GetAllocatedBytesForCurrentThread() - allocationStart));
            if (remaining <= 0)
                throw new InvalidOperationException("grammar row inspection exhausted its allocation grant");
            using var novel = IntentStage.NewBounded(0, remaining);
            EmitNativeStage(novel, witnessWeight, IngestClock.NowUnixUs(), existingBitmap);
            entities = CopyTupleParser.DecodeEntityRows([novel.TupleBuffer(IntentStageTable.Entities)])
                .ToImmutableArray();
            if (checked(all.AllocatedBytes + novel.AllocatedBytes
                + GC.GetAllocatedBytesForCurrentThread() - allocationStart) > grant)
                throw new InvalidOperationException("grammar row inspection exceeded its allocation grant");
        }
        // Constituent order is carried by Native physicality trajectories.
        return (entities, physicalities, [], root);
    }

    private Hash128 EmitNativeStage(IntentStage stage, double witnessWeight, long nowUs,
                                     byte[]? existingBitmap)
    {
        Hash128 source = _sourceId;
        int rc;
        if (existingBitmap is { Length: > 0 })
        {
            fixed (byte* bitmap = existingBitmap)
                rc = NativeInterop.ComposeDrainIntoStage(ActiveResult,
                    stage.DangerousNativeHandle, &source, nowUs, witnessWeight,
                    bitmap, checked((nuint)existingBitmap.Length * 8));
        }
        else
            rc = NativeInterop.ComposeDrainIntoStage(ActiveResult,
                stage.DangerousNativeHandle, &source, nowUs, witnessWeight, null, 0);
        if (rc != 0)
            throw new InvalidOperationException($"laplace_compose_drain_into_stage returned {rc}");
        return NativeInterop.ComposeRootId(ActiveResult);
    }

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
        EnsureComposed(existingBitmap);
        int firstPhysicality = stage.PhysicalityCount;
        Hash128 root = EmitNativeStage(stage, witnessWeight, nowUs, existingBitmap);
        stage.RecordPhysicalitySourceSince(firstPhysicality, _sourceId);
        return root;
    }

    public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight)
    => DrainInto(builder, witnessWeight, existingBitmap: null);




    public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? existingBitmap)
    {
        EnsureComposed(existingBitmap);
        ArgumentNullException.ThrowIfNull(builder);
        long nowUs = IngestClock.NowUnixUs();
        var stage = builder.ContentStage;
        int firstPhysicality = stage.PhysicalityCount;

        EmitNativeStage(stage, witnessWeight, nowUs, existingBitmap);

        stage.RecordPhysicalitySourceSince(firstPhysicality, _sourceId);

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
            builder.NoteStagedPhysicalityPlacement(ph.Id);
        }

        return NativeInterop.ComposeRootId(ActiveResult);
    }

    /// <summary>
    /// The already-composed grammar root as a constituent for a larger ordered
    /// realization such as a containing file. This reuses the native grammar
    /// placement; callers must drain this composer before they stage the parent.
    /// </summary>
    public OrderedCompositionComponent RootComponent()
    {
        EnsureComposed();
        Hash128 rootId = NativeInterop.ComposeRootId(ActiveResult);
        if (rootId == default)
            throw new InvalidOperationException("grammar composition has no root");
        double* coord = stackalloc double[4];
        byte tier = 0, hasAtom = 0;
        uint atom = 0;
        int rc = NativeInterop.ComposeRootPlacement(ActiveResult, coord, &tier, &atom, &hasAtom);
        if (rc != 0)
            throw new InvalidOperationException($"grammar root has no native placement ({rc})");
        return new OrderedCompositionComponent(rootId, tier,
            coord[0], coord[1], coord[2], coord[3], atom, hasAtom != 0);
    }

    public bool TrySpanEntity(uint startByte, uint endByte, out Hash128 id)
    {
        EnsureComposed();
        id = default;
        fixed (Hash128* p = &id)
            return NativeInterop.ComposeSpanLookup(ActiveResult, startByte, endByte, p) == 0;
    }

    public IReadOnlyList<(uint Start, uint End)> FieldSpans()
        => GrammarAstFields.FieldSpans(_ast);

    public void Dispose()
    {
        if (_disposed) return;
        lock (_sync)
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

public static class GrammarAstFields
{
    public static IReadOnlyList<(uint Start, uint End)> FieldSpans(GrammarAst ast)
    {
        var list = new List<(uint, uint)>();
        for (int i = 0; i < ast.NodeCount; i++)
        {
            var node = ast.GetNode(i);
            if (ast.NodeTypeIs(node.NodeTypeId, "field"u8))
                list.Add((node.StartByte, node.EndByte));
        }
        return list;
    }
}
