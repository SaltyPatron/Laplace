using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessCorpusSourceTests
{
    private static string Fixture(int number = 1) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", $"position-playing-game-{number}.pgn"));
    private static ChessGameRecord Parse(string text) => Assert.IsType<ChessGameRecord>(
        ChessPgnDecomposer.TryParseGame(text, requireCompleteSource: true));
    private static string Temporary()
    {
        string directory = Path.Combine(Path.GetTempPath(), "laplace-corpus-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public async Task RawSourceHashAndFramedGameHashHaveSeparateHonestIdentities()
    {
        string directory = Temporary();
        try
        {
            string path = Path.Combine(directory, "original.pgn");
            string raw = Fixture().Replace("\r\n", "\n").Replace("\n", "\r\n");
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(raw));
            var source = await ChessCorpusPreparation.IdentifyAsync(path, default);
            string framed = Assert.Single(PgnGames.StreamGames(path));
            var game = Parse(framed);
            var selected = ChessCorpusPreparation.Describe(1, game);
            ChessCorpusPreparation.ValidateSelection(selected, game);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw))), source.Sha256);
            Assert.Equal(Encoding.UTF8.GetByteCount(raw), source.Bytes);
            Assert.NotEqual(source.Sha256, selected.FramedGameSha256);
            Assert.Equal(framed, Assert.Single(ChessCorpusPreparation.ReadSelected(path, [selected], default)));
            Assert.Equal(PgnGames.TagStr(raw, "Site"), PgnGames.TagStr(game.GameText, "Site"));
            Assert.Equal(PgnGames.TagStr(raw, "Event"), PgnGames.TagStr(game.GameText, "Event"));
            Assert.Equal(154, game.MoveIds.Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SelectionReadsWholeOriginalOccurrenceAtItsSourceOrdinalWithoutRewritingHeaders()
    {
        string directory = Temporary();
        try
        {
            string path = Path.Combine(directory, "original.pgn");
            await File.WriteAllTextAsync(path, Fixture(1) + "\n" + Fixture(2));
            var frames = PgnGames.StreamGames(path).ToArray();
            Assert.Equal(2, frames.Length);
            var expected = ChessCorpusPreparation.Describe(2, Parse(frames[1]));
            string reread = Assert.Single(ChessCorpusPreparation.ReadSelected(path, [expected], default));
            Assert.Equal(frames[1], reread);
            ChessCorpusPreparation.ValidateSelection(expected, Parse(reread));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ChangedRawSourceAndChangedSelectedTextAreBothRejected()
    {
        string directory = Temporary();
        try
        {
            string path = Path.Combine(directory, "original.pgn");
            await File.WriteAllTextAsync(path, Fixture());
            var source = await ChessCorpusPreparation.IdentifyAsync(path, default);
            string frame = Assert.Single(PgnGames.StreamGames(path));
            var expected = ChessCorpusPreparation.Describe(1, Parse(frame));
            // A header change is not an authorized way to create another novel PLAYING.
            string changed = frame.Replace("[Site \"", "[Site \"altered-");
            await File.WriteAllTextAsync(path, changed);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ChessCorpusPreparation.RequireUnchangedAsync(source, default));
            Assert.Throws<InvalidDataException>(() =>
                ChessCorpusPreparation.ReadSelected(path, [expected], default).ToArray());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SameLengthMutationIsNotHiddenByFileLengthOrPath()
    {
        string directory = Temporary();
        try
        {
            string path = Path.Combine(directory, "original.pgn");
            await File.WriteAllTextAsync(path, Fixture());
            var before = await ChessCorpusPreparation.IdentifyAsync(path, default);
            byte[] data = await File.ReadAllBytesAsync(path);
            data[0] = data[0] == (byte)'[' ? (byte)'!' : (byte)'[';
            await File.WriteAllBytesAsync(path, data);
            Assert.Equal(before.Bytes, new FileInfo(path).Length);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ChessCorpusPreparation.RequireUnchangedAsync(before, default));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedUtf8CannotCreateReplacementCharacterIdentities(bool withBom)
    {
        string directory = Temporary();
        try
        {
            string path = Path.Combine(directory, "original.pgn");
            var game = Parse(Fixture());
            var expected = ChessCorpusPreparation.Describe(1, game);
            byte[] bytes = Encoding.UTF8.GetBytes(game.GameText);
            bytes[Math.Min(20, bytes.Length - 1)] = 0xff;
            if (withBom) bytes = [0xef, 0xbb, 0xbf, .. bytes];
            await File.WriteAllBytesAsync(path, bytes);
            Assert.Throws<DecoderFallbackException>(() =>
                ChessCorpusPreparation.ReadSelected(path, [expected], default).ToArray());
        }
        finally { Directory.Delete(directory, true); }
    }


    [Fact]
    public async Task ValidUtf8BomPreservesFramingAndCanonicalSourceIdentity()
    {
        string directory = Temporary();
        try
        {
            string path = Path.Combine(directory, "original.pgn");
            string original = Fixture().Replace("\r\n", "\n");
            await File.WriteAllTextAsync(path, original, new UTF8Encoding(true, true));
            string framed = Assert.Single(PgnGames.StreamGames(path, requireUtf8: true));
            var expected = Parse(original);
            var actual = Parse(framed);
            Assert.Equal(expected.PlayingId, actual.PlayingId);
            Assert.Equal(expected.LineId, actual.LineId);
            Assert.Equal(original.TrimEnd(), framed.TrimEnd());
            var selected = ChessCorpusPreparation.Describe(1, actual);
            Assert.Equal(framed, Assert.Single(ChessCorpusPreparation.ReadSelected(path, [selected], default)));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utf16BomCannotSilentlyChangeTheMeasuredUtf8Scope(bool bigEndian)
    {
        string directory = Temporary();
        try
        {
            string path = Path.Combine(directory, "original.pgn");
            var game = Parse(Fixture());
            var expected = ChessCorpusPreparation.Describe(1, game);
            await File.WriteAllTextAsync(path, game.GameText, new UnicodeEncoding(bigEndian, true, true));
            Assert.Throws<InvalidDataException>(() =>
                ChessCorpusPreparation.ReadSelected(path, [expected], default).ToArray());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ReorderedOrdinalsAndDuplicateCanonicalOccurrencesRejectBeforeSourceRead()
    {
        var expected = ChessCorpusPreparation.Describe(1, Parse(Fixture()));
        string absent = Path.Combine(Path.GetTempPath(), "absent-corpus-" + Guid.NewGuid().ToString("N") + ".pgn");
        foreach (var entries in new[]
        {
            new[] { expected, expected with { SourceOrdinal = 2 } },
            new[] { expected with { SourceOrdinal = 2 }, expected with { PlayingId = new string('a', 32) } },
            new[] { expected with { SourceOrdinal = 0 } },
        })
            Assert.Throws<InvalidDataException>(() =>
                ChessCorpusPreparation.ReadSelected(absent, entries, default).ToArray());
    }

    [Theory]
    [InlineData("playing")]
    [InlineData("line")]
    [InlineData("start")]
    [InlineData("plies")]
    [InlineData("result")]
    [InlineData("text")]
    public void PreparedIdentityMustAgreeWithTheSharedNativeParse(string field)
    {
        var game = Parse(Fixture());
        var expected = ChessCorpusPreparation.Describe(1, game);
        var altered = field switch
        {
            "playing" => expected with { PlayingId = new string('a', 32) },
            "line" => expected with { LineId = new string('b', 32) },
            "start" => expected with { StartPositionId = new string('c', 32) },
            "plies" => expected with { Plies = expected.Plies - 1 },
            "result" => expected with { Result = "*" },
            _ => expected with { FramedGameSha256 = new string('d', 64) },
        };
        Assert.Throws<InvalidDataException>(() => ChessCorpusPreparation.ValidateSelection(altered, game));
        var ordinary = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(game.GameText));
        Assert.Throws<InvalidDataException>(() => ChessCorpusPreparation.ValidateSelection(expected, ordinary));
    }

    [Fact]
    public async Task ExistingEvidenceDirectoryCannotBeReusedOrChangedBeforeAnyDatabaseWork()
    {
        string directory = Temporary();
        try
        {
            string path = Path.Combine(directory, "original.pgn");
            await File.WriteAllTextAsync(path, Fixture());
            string evidence = Path.Combine(directory, "existing");
            Directory.CreateDirectory(evidence);
            string marker = Path.Combine(evidence, "keep.txt");
            await File.WriteAllTextAsync(marker, "original evidence");
            await Assert.ThrowsAsync<IOException>(() =>
                ChessCorpusBenchmark.RunAsync(new(path, evidence, Games: 2)));
            Assert.Equal("original evidence", await File.ReadAllTextAsync(marker));
            Assert.Single(Directory.GetFiles(evidence));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FailingCleanup(Exception failure) : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.FromException(failure);
        }
    }

    [Fact]
    public async Task CompletedBodyCannotHideALateOwnedDisposalFailure()
    {
        var owner = new FailingCleanup(new IOException("retained cleanup failed"));
        bool bodyCompleted = false;
        var failure = await ChessCorpusBenchmark.CaptureFailureAsync(async () =>
        {
            await using var selected = owner;
            await Task.Yield();
            bodyCompleted = true;
        });
        Assert.True(bodyCompleted);
        Assert.True(owner.Disposed);
        Assert.NotNull(failure);
        Assert.Equal("failed", failure.Status);
        Assert.Equal(nameof(IOException), failure.ErrorType);
        Assert.Equal("retained cleanup failed", failure.Error);
    }

    [Fact]
    public async Task LateOwnedCancellationIsRetainedAsAnUnqualifiedCancellation()
    {
        var owner = new FailingCleanup(new OperationCanceledException("late cancellation"));
        var failure = await ChessCorpusBenchmark.CaptureFailureAsync(async () =>
        {
            await using var selected = owner;
            await Task.Yield();
        });
        Assert.True(owner.Disposed);
        Assert.NotNull(failure);
        Assert.Equal("cancelled", failure.Status);
        Assert.Equal(nameof(OperationCanceledException), failure.ErrorType);
        Assert.Null(await ChessCorpusBenchmark.CaptureFailureAsync(() => Task.CompletedTask));
    }

}
