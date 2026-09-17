using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessGraphPresenceObservationTests
{
    private sealed class EntityPresenceReader(params Hash128[] present) : ISubstrateReader
    {
        private readonly HashSet<Hash128> _present = [.. present];
        public bool IsProvenPresent(Hash128 id) => _present.Contains(id);
        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder,
            CancellationToken ct = default) => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates,
            CancellationToken ct = default)
        {
            var bitmap = new byte[(candidates.Count + 7) / 8];
            for (int i = 0; i < candidates.Count; i++)
                if (_present.Contains(candidates[i])) bitmap[i >> 3] |= (byte)(1 << (i & 7));
            return Task.FromResult(bitmap);
        }
    }

    private static (ChessNode Root, ChessNode[] Nodes, Action<SubstrateChangeBuilder, Hash128, long> Emit)
        Input(bool position)
    {
        var board = Board.FromFen(ChessModality.StartFen);
        if (position)
        {
            var composed = ChessCompose.Position(board);
            return (composed.Position, [.. composed.Substructures, composed.Position],
                (builder, source, _) => ChessGraph.EmitComposed(builder, composed, source));
        }
        var move = San.Resolve(board, "e4", new List<ChessMove>())
            ?? throw new InvalidOperationException("fixture move is not legal");
        var moved = ChessCompose.Move(Piece.WPawn, move);
        return (moved.Move, [.. moved.Fields, moved.Move],
            (builder, source, observed) => ChessGraph.EmitMove(builder, Piece.WPawn, move, source, observed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PresentRootWithMissingDescendantsStillOffersEveryExactForm(bool position)
    {
        var input = Input(position);
        var source = ChessVocabulary.PgnSourceId;
        var reader = new EntityPresenceReader(input.Root.Id);
        using var builder = new SubstrateChangeBuilder(source, "chess/existing-root")
            .DeclareSourcePrior(SourceTrust.StructuredCorpus).SetPresenceOracle(reader);

        input.Emit(builder, source, 1_000_000);
        var change = builder.Build();
        Assert.True(reader.IsProvenPresent(input.Root.Id));
        Assert.Contains(input.Nodes, node => !reader.IsProvenPresent(node.Id));
        Assert.Equal(input.Nodes.Select(node => node.Id).Distinct().OrderBy(id => id.ToString()),
            change.Entities.Select(row => row.Id).OrderBy(id => id.ToString()));
        Assert.Equal(input.Nodes.Length, change.PhysicalityObservations.Length);
        foreach (var node in input.Nodes)
            AssertBody(node, Assert.Single(change.PhysicalityObservations,
                row => row.EntityId == node.Id));
        Assert.Equal(input.Root.Id, Input(position).Root.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusedEntitiesKeepSourceFormsWithoutDuplicatingSelectedEntitiesOrPlacements(bool position)
    {
        var input = Input(position);
        var firstSource = ChessVocabulary.PgnSourceId;
        var secondSource = ChessVocabulary.AnalysisSourceId;
        var reader = new EntityPresenceReader(input.Nodes.Select(node => node.Id).ToArray());
        using var builder = new SubstrateChangeBuilder(firstSource, "chess/reused-forms")
            .DeclareSourcePrior(firstSource, SourceTrust.StructuredCorpus)
            .DeclareSourcePrior(secondSource, SourceTrust.StructuredCorpus)
            .SetPresenceOracle(reader);

        input.Emit(builder, firstSource, 1_000_000);
        input.Emit(builder, firstSource, 1_000_001);
        input.Emit(builder, secondSource, 1_000_002);
        var change = builder.Build();
        int uniqueNodes = input.Nodes.Select(node => node.Id).Distinct().Count();
        Assert.Equal(uniqueNodes, change.Entities.Length);
        Assert.Equal(uniqueNodes, change.Physicalities.Length);
        Assert.Equal(3 * input.Nodes.Length, change.PhysicalityObservations.Length);
        Assert.Equal(2 * input.Nodes.Length,
            change.PhysicalityObservations.Count(row => row.SourceId == firstSource));
        Assert.Equal(input.Nodes.Length,
            change.PhysicalityObservations.Count(row => row.SourceId == secondSource));
        foreach (var node in input.Nodes)
            Assert.All(change.PhysicalityObservations.Where(row => row.EntityId == node.Id),
                row => AssertBody(node, row));
        Assert.Empty(change.Attestations);
        // Raw source occurrences are retained here. Native descriptor admission and
        // the durable source-unit journal own evidence deduplication, not E presence.
    }

    private static void AssertBody(ChessNode expected, PhysicalityRow actual)
    {
        Assert.Equal(expected.Id, actual.EntityId);
        Assert.Equal(expected.PhysId, actual.Id);
        Assert.Equal(PhysicalityType.Content, actual.Type);
        Assert.Equal(expected.NConstituents, actual.NConstituents);
        Assert.Equal(expected.Coord.Select(BitConverter.DoubleToInt64Bits),
            new[] { actual.CoordX, actual.CoordY, actual.CoordZ, actual.CoordM }
                .Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(0, expected.Hb.CompareToBytewise(actual.HilbertIndex));
        Assert.Equal(expected.Trajectory.Select(BitConverter.DoubleToInt64Bits),
            actual.TrajectoryXyzm!.Select(BitConverter.DoubleToInt64Bits));
        Assert.Null(actual.AlignmentResidual);
        Assert.Null(actual.SourceDim);
    }
}
