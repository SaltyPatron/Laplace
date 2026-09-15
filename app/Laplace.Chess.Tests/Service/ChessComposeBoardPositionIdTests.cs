using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using System.Buffers.Binary;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// Interchange text and the binary board path must resolve to one typed position composition.
/// </summary>
public sealed class ChessComposeBoardPositionIdTests
{
    [Fact]
    public void BoardPositionId_MatchesSurfacePath_StartAndPlies()
    {
        var m = new ChessModality();
        lock (ChessCompose.Gate)
        {
            var state = m.Initial();
            AssertEqual(state);

            foreach (var san in new[] { "e4", "e5", "Nf3", "Nc6", "Bb5" })
            {
                var mv = San.Resolve(state.Board, m.LegalActions(state), san);
                Assert.NotNull(mv);
                state = m.Apply(state, mv!.Value);
                AssertEqual(state);
            }
        }
    }

    [Fact]
    public void PositionPhysicality_IsTypedBoardTrajectory_NotTextSentence()
    {
        var board = Board.FromFen(ChessModality.StartFen);
        var composed = ChessCompose.Position(board);

        Assert.Equal(35, composed.Position.NConstituents); // 3 state atoms + 32 pieces
        Assert.Equal(composed.Substructures.Select(static n => n.Id).ToArray(),
            Trajectory.Constituents(composed.Position.Trajectory).ToArray());
        Assert.All(composed.Substructures, atom =>
            Assert.Equal(5, atom.NConstituents)); // domain + two tagged nibbles per ushort
        Assert.Equal(0x0F, ChessPositionIdentity.CastlingDestinationMask(board));
    }

    [Fact]
    public void Chess960_UsesTheSameFourCastlingDestinationBits()
    {
        var board = Board.FromFen(
            "nqrkbbrn/pppppppp/8/8/8/8/PPPPPPPP/NQRKBBRN w GCgc - 0 1");
        Assert.Equal(0x0F, ChessPositionIdentity.CastlingDestinationMask(board));
        Assert.Equal(35, ChessCompose.Position(board).Position.NConstituents);
    }

    [Fact]
    public void NativeFloorHitPreservesCompletePositionBytesAndAvoidsChildCoordinateAllocation()
    {
        string directory = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")
            ?? throw new InvalidOperationException("TMPDIR must identify the permanent test workspace"),
            "chess-position-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "position.bin");
        try
        {
            ChessCompose.InitializePerfcaches();
            ChessPositionFloor.Unload();
            var board = Board.FromFen(ChessModality.StartFen);
            var expected = ChessCompose.Position(board);
            _ = ChessCompose.Position(board); // finish first-call/JIT work before allocation comparison
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10; i++) _ = ChessCompose.Position(board);
            long missBytes = GC.GetAllocatedBytesForCurrentThread() - start;

            // A fixture containing the actual native-derived position, serialized in
            // the existing floor format and authenticated by its native body hash.
            byte[] body = new byte[128 + 80];
            BinaryPrimitives.WriteUInt32LittleEndian(body, 0x5048434c);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(16), 80);
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(24), 128);
            expected.Position.Id.WriteBytes(body.AsSpan(128, 16));
            for (int i = 0; i < 4; i++)
                BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(144 + i * 8),
                    BitConverter.DoubleToInt64Bits(expected.Position.Coord[i]));
            expected.Position.Hb.WriteBytes(body.AsSpan(176, 16));
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(192), (uint)expected.Position.NConstituents);
            body[196] = expected.Position.Tier;
            File.WriteAllBytes(path, [.. body, .. Hash128.Blake3(body).ToBytes()]);
            ChessPositionFloor.Load(path);
            Assert.True(ChessPositionFloor.IsLoaded);
            Assert.Equal(1, ChessPositionFloor.RecordCount);
            var actual = ChessCompose.Position(board);
            Assert.Equal(expected.Position.Id, actual.Position.Id);
            Assert.Equal(expected.Position.PhysId, actual.Position.PhysId);
            Assert.Equal(expected.Position.Trajectory.Select(BitConverter.DoubleToInt64Bits),
                actual.Position.Trajectory.Select(BitConverter.DoubleToInt64Bits));
            Assert.Equal(expected.Position.Coord.Select(BitConverter.DoubleToInt64Bits),
                actual.Position.Coord.Select(BitConverter.DoubleToInt64Bits));
            Assert.Equal(0, expected.Position.Hb.CompareToBytewise(actual.Position.Hb));
            Assert.Equal(expected.Substructures.Select(n => n.Id), actual.Substructures.Select(n => n.Id));
            start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10; i++) _ = ChessCompose.Position(board);
            long hitBytes = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.True(missBytes - hitBytes >= 10L * expected.Substructures.Count * 4 * sizeof(double),
                $"floor miss allocated {missBytes} bytes; hit allocated {hitBytes}");
        }
        finally
        {
            ChessPositionFloor.Unload();
            ChessPositionFloor.LoadDefault();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertEqual(ChessState state)
    {
        string surface = state.Board is var b
            ? PositionContent.Surface(b, Ep(b))
            : throw new InvalidOperationException();
        Assert.Equal(ChessCompose.PositionId(surface), ChessCompose.PositionId(state.Board));
    }

    private static string Ep(Board b)
    {
        int ep = ChessModality.CapturableEpSquare(b);
        return ep < 0 ? "-" : Board.SquareToAlgebraic(ep);
    }
}
