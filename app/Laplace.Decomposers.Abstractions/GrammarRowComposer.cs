using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>One row: native compose + span index for witness field mapping.</summary>
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

    public (ImmutableArray<EntityRow> Entities,
            ImmutableArray<PhysicalityRow> Physicalities,
            ImmutableArray<AttestationRow> Precedes,
            Hash128 RootId) Materialize(double witnessWeight)
    {
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var entities = ImmutableArray.CreateBuilder<EntityRow>();
        var physicalities = ImmutableArray.CreateBuilder<PhysicalityRow>();
        var precedes = ImmutableArray.CreateBuilder<AttestationRow>();

        nuint nEnt = NativeInterop.ComposeEntityCount(_compose);
        for (nuint i = 0; i < nEnt; i++)
        {
            NativeInterop.ComposeEntityNative e;
            NativeInterop.ComposeGetEntity(_compose, i, &e);
            entities.Add(new EntityRow(e.Id, e.Tier, e.TypeId, _sourceId));
        }

        nuint nPhys = NativeInterop.ComposePhysicalityCount(_compose);
        for (nuint i = 0; i < nPhys; i++)
        {
            NativeInterop.ComposePhysicalityNative ph;
            NativeInterop.ComposeGetPhysicality(_compose, i, &ph);
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
            precedes.Add(AttestationFactory.CreateAggregated(
                pr.SubjectId, GrammarEntityBuilder.PrecedesTypeId, pr.ObjectId,
                _sourceId, contextId: null,
                games: pr.Games, sumScoreFp1e9: sumScore, witnessWeight: witnessWeight));
        }

        return (entities.ToImmutable(), physicalities.ToImmutable(),
                precedes.ToImmutable(), NativeInterop.ComposeRootId(_compose));
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
