using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Xunit;
using Owner = Laplace.Chess.Service.ChessRecordedFloorWitness;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessRecordedFloorWitnessTests
{
    [Fact]
    public void RealNativePositionAndPersistentTransitionAreComparedToFullTypedReplay()
    {
        using var files = new Fixture();
        var result = Owner.Verify(files.Options);
        Assert.Equal("passed", result.Status);
        Assert.Equal("e2e4", result.Selected.MoveUci);
        Assert.Equal(1, result.Selected.Ply);
        Assert.Equal(Owner.Hex(files.Start.Id), result.Selected.FromPositionId);
        Assert.Equal(Owner.Hex(files.Next), result.Selected.ToPositionId);
        Assert.Contains("e2e4", result.Selected.LegalUci);
        Assert.Equal(Owner.Fact(files.Options.WitnessedInputs), result.Files["witnessed_inputs"]);
        Assert.Single(result.NativeModules);
        Assert.False(ChessPositionFloor.IsLoaded);
        Assert.False(ChessTransitionFloor.IsLoaded);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("line")]
    [InlineData("move")]
    [InlineData("san")]
    [InlineData("result")]
    public void AlteredWitnessCannotPassCanonicalReplay(string change)
    {
        using var files = new Fixture();
        var value = files.Witness;
        value[change switch { "start" => "StartPositionId", "line" => "LineId",
            "move" => "MoveIds", "san" => "Moves", _ => "Result" }] = change switch
        {
            "move" => new[] { new Hash128(1, 2).ToString() },
            "san" => new[] { "d4" },
            "result" => "*",
            _ => new Hash128(3, 4).ToString()
        };
        if (change == "move")
            value["LineId"] = ChessCompose.LineId(files.Start.Id, [new Hash128(1, 2)]).ToString();
        files.SaveWitness();
        Assert.Throws<InvalidDataException>(() => Owner.Verify(files.Options));
        Assert.False(File.Exists(files.Options.Output));
    }

    [Fact]
    public void SeedCoverageAndIncorrectInstalledResultRefuseInsteadOfClaimingCorpusHit()
    {
        using var files = new Fixture();
        WritePosition(files.Options.SeedPosition, files.Start);
        Assert.Throws<InvalidDataException>(() => Owner.Verify(files.Options));
        WritePosition(files.Options.SeedPosition);
        ChessTransitionFloor.WriteBlob(files.Options.InstalledTransition,
            [(files.Key, new Hash128(1, 2))]);
        Assert.Throws<InvalidDataException>(() => Owner.Verify(files.Options));
    }

    [Fact]
    public void NativeGeometryMismatchCannotBeHiddenByMatchingContentId()
    {
        using var files = new Fixture();
        var wrong = files.Start with { Coord = [0, 0, 0, 0] };
        WritePosition(files.Options.InstalledPosition, wrong);
        Assert.Throws<InvalidDataException>(() => Owner.Verify(files.Options));
    }

    [Fact]
    public void FramingAndReplayAllowancesRejectRealInputBeforeCompletion()
    {
        using var files = new Fixture();
        Assert.Throws<InvalidDataException>(() => Owner.Verify(files.Options with { MaximumLineBytes = 16 }));
        Assert.Throws<InvalidDataException>(() => Owner.Verify(files.Options with { MaximumReplayBytes = 1 }));
        File.WriteAllText(files.Options.WitnessedInputs, JsonSerializer.Serialize(files.Witness));
        Assert.Throws<InvalidDataException>(() => Owner.Verify(files.Options));
    }

    [Fact]
    public void ExistingRecordStructTransportAndRawByteHexRecoverTheSameIdentity()
    {
        var value = new Hash128(0xFEDCBA9876543210, 0x0123456789ABCDEF);
        Assert.Equal(value, Owner.Id(value.ToString()));
        Assert.Equal(value, Owner.Id(Owner.Hex(value)));
        Assert.ThrowsAny<Exception>(() => Owner.Id("Hash128 { Hi = 01, Lo = 2 }"));
        Assert.ThrowsAny<Exception>(() => Owner.Id("ab"));
    }

    private static void WritePosition(string path, params ChessNode[] positions)
    {
        var nodes = positions.OrderBy(n => n.Id, Comparer<Hash128>.Create((a, b) => a.CompareToBytewise(b))).ToArray();
        byte[] bytes = new byte[128 + nodes.Length * 80 + 16];
        "LCHP"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), (ulong)nodes.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 80);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 128);
        Encoding.ASCII.GetBytes("catalog").CopyTo(bytes, 48);
        for (int i = 0; i < nodes.Length; i++)
        {
            var n = nodes[i]; Span<byte> record = bytes.AsSpan(128 + i * 80, 80);
            n.Id.WriteBytes(record);
            for (int j = 0; j < 4; j++)
                BinaryPrimitives.WriteInt64LittleEndian(record[(16 + j * 8)..], BitConverter.DoubleToInt64Bits(n.Coord[j]));
            var hilbert = n.Hb;
            System.Runtime.InteropServices.MemoryMarshal.Write(record[48..], in hilbert);
            BinaryPrimitives.WriteUInt32LittleEndian(record[64..], checked((uint)n.NConstituents));
            record[68] = n.Tier;
        }
        Hash128.Blake3(bytes.AsSpan(0, bytes.Length - 16)).WriteBytes(bytes.AsSpan(bytes.Length - 16));
        File.WriteAllBytes(path, bytes);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")
            ?? throw new InvalidOperationException("TMPDIR must identify the permanent test workspace"),
            "chess-floor-witness-" + Guid.NewGuid().ToString("N"));
        public Owner.Options Options { get; }
        public ChessNode Start { get; }
        public Hash128 Next { get; }
        public Hash128 Key { get; }
        public Dictionary<string, object?> Witness { get; }
        public Fixture()
        {
            Directory.CreateDirectory(root);
            Options = new(Path.Combine(root, "witnessed.jsonl"), Path.Combine(root, "seed-position.bin"),
                Path.Combine(root, "seed-transition.bin"), Path.Combine(root, "installed-position.bin"),
                Path.Combine(root, "installed-transition.bin"), Path.Combine(root, "selected.json"));
            ChessCompose.InitializePerfcaches();
            ChessPositionFloor.Unload(); ChessTransitionFloor.Unload();
            var board = Board.FromFen(ChessModality.StartFen);
            Start = ChessCompose.Position(board).Position;
            var move = San.Resolve(board, "e4", new List<ChessMove>())!.Value;
            var moveId = ChessCompose.MoveId(board.Squares[move.From], move);
            Key = ChessCompose.TransitionKey(Start.Id, moveId);
            MoveApply.Make(board, move);
            Next = ChessCompose.PositionId(board);
            Witness = new()
            {
                ["PlayingId"] = new Hash128(50, 60).ToString(),
                ["LineId"] = ChessCompose.LineId(Start.Id, [moveId]).ToString(),
                ["StartPositionId"] = Start.Id.ToString(), ["StartFen"] = ChessModality.StartFen,
                ["MoveIds"] = new[] { moveId.ToString() }, ["Moves"] = new[] { "e4" },
                ["Result"] = "1/2-1/2"
            };
            SaveWitness();
            WritePosition(Options.SeedPosition);
            WritePosition(Options.InstalledPosition, Start);
            ChessTransitionFloor.WriteBlob(Options.SeedTransition, []);
            ChessTransitionFloor.WriteBlob(Options.InstalledTransition, [(Key, Next)]);
        }
        public void SaveWitness() => File.WriteAllText(Options.WitnessedInputs, JsonSerializer.Serialize(Witness) + "\n");
        public void Dispose()
        {
            ChessPositionFloor.Unload(); ChessTransitionFloor.Unload();
            Directory.Delete(root, recursive: true);
        }
    }
}
