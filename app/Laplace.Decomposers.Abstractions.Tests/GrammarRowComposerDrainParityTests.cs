using System.Collections.Immutable;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Phase 1 safety gate: GrammarRowComposer.DrainInto (native-direct, no managed marshal) must
/// produce byte-for-byte identical entity + physicality COPY blobs to Materialize + re-stage,
/// for the same observed-at. Proves the one-hop path is a faithful replacement before any caller
/// is migrated off Materialize.
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class GrammarRowComposerDrainParityTests
{
    private static readonly Hash128 Src =
        Hash128.OfCanonical("substrate/source/test/drain-parity/v1");
    private const long NowUs = 1_700_000_000_000_000L;

    [Theory]
    [InlineData("1\tRelatedTo\t/c/en/dog\t/c/en/animal\t{}")]
    [InlineData("7\tIsA\t/c/en/a moment in time\t/c/en/moment\t{}")]
    public void DrainInto_Matches_Materialize_ByteForByte(string row)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(row);
        using var ast = GrammarDecomposer.Parse(utf8, "tsv");
        using var composer = new GrammarRowComposer(utf8, ast, Src, "tsv");

        // managed path: Materialize → re-stage with a fixed observed-at (entities have no
        // timestamp; physicalities do, so we pin NowUs on both sides for a fair byte compare).
        var (ents, phys, prec, root) = composer.Materialize(1.0);
        Assert.True(ents.Length > 0, "expected the tsv row to compose at least one entity");

        using var expected = IntentStage.New(Math.Max(16, ents.Length + phys.Length));
        foreach (var e in ents)
            expected.AddEntity(e.Id, e.Tier, e.TypeId, e.FirstObservedBy);
        Span<double> coord = stackalloc double[4];
        foreach (var p in phys)
        {
            coord[0] = p.CoordX; coord[1] = p.CoordY; coord[2] = p.CoordZ; coord[3] = p.CoordM;
            expected.AddPhysicality(
                p.Id, p.EntityId, p.SourceId, (short)p.Type, coord, p.HilbertIndex,
                p.TrajectoryXyzm ?? ReadOnlySpan<double>.Empty, p.NConstituents,
                p.AlignmentResidual, p.SourceDim, NowUs);
        }

        // native-direct path
        var precOut = ImmutableArray.CreateBuilder<AttestationRow>();
        using var actual = IntentStage.New(Math.Max(16, ents.Length + phys.Length));
        Hash128 rootDrain = composer.DrainInto(actual, 1.0, NowUs, precOut);

        Assert.Equal(root, rootDrain);
        Assert.Equal(expected.EntityCount, actual.EntityCount);
        Assert.Equal(expected.PhysicalityCount, actual.PhysicalityCount);
        Assert.Equal(
            expected.EmitCopyBinary(IntentStageTable.Entities),
            actual.EmitCopyBinary(IntentStageTable.Entities));
        Assert.Equal(
            expected.EmitCopyBinary(IntentStageTable.Physicalities),
            actual.EmitCopyBinary(IntentStageTable.Physicalities));

        // PRECEDES ride as managed rows (Phase 1); their content-addressed ids must match.
        Assert.Equal(prec.Length, precOut.Count);
        for (int i = 0; i < prec.Length; i++)
        {
            Assert.Equal(prec[i].Id, precOut[i].Id);
            Assert.Equal(prec[i].SubjectId, precOut[i].SubjectId);
            Assert.Equal(prec[i].ObjectId, precOut[i].ObjectId);
            Assert.Equal(prec[i].ObservationCount, precOut[i].ObservationCount);
        }
    }
}
