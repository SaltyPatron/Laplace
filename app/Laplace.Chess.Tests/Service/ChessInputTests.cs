using System.IO.Compression;
using System.Text;
using Laplace.Chess.Service;
using Laplace.Engine.Core.IO;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// The zero-input contract. Before <see cref="ChessInput"/>, every chess lane resolved an
/// empty file list to an empty record stream, and the CLI exited 0 having written nothing:
///
///   laplace ingest chess /vault/Data/Games/Chess/Lumbras   ->  EXIT=0, 0 entities
///
/// (18 GB of games one directory down.) These tests pin the failure, and pin that the
/// message names the fix rather than merely reporting emptiness.
/// </summary>
public sealed class ChessInputTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "chess-input-" + Guid.NewGuid().ToString("N"));

    public ChessInputTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Dir(string name)
    {
        string p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private const string OneGame =
        "[Event \"Test\"]\n[White \"A\"]\n[Black \"B\"]\n[Result \"1-0\"]\n\n1. e4 e5 2. Nf3 1-0\n";

    // Native zstd --check frame, 95 compressed bytes for 151 UTF-8 bytes. Its
    // content size is deliberately unknown, as for streamed Lichess downloads.
    private static readonly byte[] TwoGamesZstd = Convert.FromHexString(
        "28b52ffd0458950200d2c4111980b939e8ffff81055e128980723abab4cdcceebd9136e8767acb5d3aca47a131329ca811355277f974d4bd4cd11480975f378230600b2fafdcba0783002fbf4d03f72674c1600200805398a70e661a3a1174");
    private static string TwoGames => OneGame + "\n" + OneGame.Replace("\"A\"", "\"C\"");

    [Fact]
    public void EmptyDirectory_Throws_NotSilentlyEmpty()
    {
        var dir = Dir("empty");
        var ex = Assert.Throws<ChessInputException>(
            () => ChessInput.Resolve(dir, SearchOption.TopDirectoryOnly, ChessInput.PgnExtensions, "chess"));
        Assert.Contains("no input files", ex.Message);
        Assert.Contains(dir, ex.Message);
    }

    [Fact]
    public void MissingPath_Throws()
    {
        var ex = Assert.Throws<ChessInputException>(() => ChessInput.Resolve(
            Path.Combine(_root, "nope"), SearchOption.TopDirectoryOnly, ChessInput.PgnExtensions, "chess"));
        Assert.Contains("does not exist", ex.Message);
    }

    /// <summary>The Lumbras shape: corpus one level down, non-recursive scope.</summary>
    [Fact]
    public void FilesOnlyInSubdirectories_ErrorNamesRecursive()
    {
        var dir = Dir("nested");
        var sub = Path.Combine(dir, "otb");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "games.pgn"), OneGame);

        var ex = Assert.Throws<ChessInputException>(
            () => ChessInput.Resolve(dir, SearchOption.TopDirectoryOnly, ChessInput.PgnExtensions, "chess"));
        Assert.Contains("--recursive", ex.Message);
        Assert.Contains("subdirectories", ex.Message);

        // ...and the same directory resolves once recursion is asked for.
        var hits = ChessInput.Resolve(dir, SearchOption.AllDirectories, ChessInput.PgnExtensions, "chess");
        Assert.Single(hits);
    }

    /// <summary>The other Lumbras shape: the corpus is still inside .7z archives.</summary>
    [Fact]
    public void UnsupportedArchivesOnly_ErrorNamesExtractCommand()
    {
        var dir = Dir("archives");
        File.WriteAllText(Path.Combine(dir, "LumbrasGigaBase_OTB_2025.7z"), "not really 7z");

        var ex = Assert.Throws<ChessInputException>(
            () => ChessInput.Resolve(dir, SearchOption.TopDirectoryOnly, ChessInput.PgnExtensions, "chess"));
        Assert.Contains(".7z", ex.Message);
        Assert.Contains("7z x", ex.Message);
    }

    [Fact]
    public void WrongExtensions_ErrorListsWhatIsThere()
    {
        var dir = Dir("wrong");
        File.WriteAllText(Path.Combine(dir, "a.csv"), "x");
        File.WriteAllText(Path.Combine(dir, "b.csv"), "x");

        var ex = Assert.Throws<ChessInputException>(
            () => ChessInput.Resolve(dir, SearchOption.TopDirectoryOnly, ChessInput.PgnExtensions, "chess"));
        Assert.Contains("2x .csv", ex.Message);
    }

    /// <summary>An explicit file is honoured whatever it is called — the parser is the gate.</summary>
    [Fact]
    public void ExplicitFile_IsHonouredRegardlessOfExtension()
    {
        string f = Path.Combine(_root, "games.txt");
        File.WriteAllText(f, OneGame);
        var hits = ChessInput.Resolve(f, SearchOption.TopDirectoryOnly, ChessInput.PgnExtensions, "chess");
        Assert.Equal(Path.GetFullPath(f), Assert.Single(hits));
    }

    /// <summary>TWIC ships each weekly issue as a .zip holding one .pgn.</summary>
    [Fact]
    public async Task ZipMember_IsReadAsGames()
    {
        var dir = Dir("zipped");
        string zipPath = Path.Combine(dir, "twic9999.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("twic9999.pgn");
            using var s = entry.Open();
            using var w = new StreamWriter(s, Encoding.UTF8);
            w.Write(OneGame + "\n" + OneGame.Replace("\"A\"", "\"C\""));
        }

        var hits = ChessInput.Resolve(dir, SearchOption.TopDirectoryOnly, ChessInput.PgnExtensions, "chess");
        Assert.Single(hits);

        var games = new List<string>();
        await foreach (var g in ChessPgnDecomposer.StreamAllGamesAsync(
                           dir, SearchOption.TopDirectoryOnly, default))
            games.Add(g);
        Assert.Equal(2, games.Count);
        Assert.All(games, g => Assert.NotNull(ChessPgnDecomposer.TryParseGame(g)));
    }

    [Fact]
    public async Task GzipMember_IsReadAsGames()
    {
        var dir = Dir("gzipped");
        string gzPath = Path.Combine(dir, "games.pgn.gz");
        using (var fs = File.Create(gzPath))
        using (var gz = new GZipStream(fs, CompressionMode.Compress))
        using (var w = new StreamWriter(gz, Encoding.UTF8))
            w.Write(OneGame);

        var games = new List<string>();
        await foreach (var g in ChessPgnDecomposer.StreamAllGamesAsync(
                           dir, SearchOption.TopDirectoryOnly, default))
            games.Add(g);
        Assert.Single(games);
    }

    [Theory]
    [InlineData("games.pgn.zst")]
    [InlineData("games.ZST")]
    public async Task ZstdMember_DirectoryAndExplicitInput_ReadSameGamesAsPlaintext(string name)
    {
        string dir = Dir("zstandard");
        string compressed = Path.Combine(dir, name);
        File.WriteAllBytes(compressed, TwoGamesZstd);
        Assert.Equal(compressed, Assert.Single(ChessInput.Resolve(dir,
            SearchOption.TopDirectoryOnly, ChessInput.PgnExtensions, "chess")));
        Assert.True(ChessInput.IsCompressed(compressed));
        string plain = Path.Combine(_root, "reference.pgn");
        File.WriteAllText(plain, TwoGames);
        var expected = new List<string>();
        await foreach (var game in ChessPgnDecomposer.StreamFileGamesAsync(plain, default)) expected.Add(game);
        var actual = new List<string>();
        await foreach (var game in ChessPgnDecomposer.StreamFileGamesAsync(compressed, default)) actual.Add(game);
        Assert.Equal(2, actual.Count);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Zstd_ConcatenatedFrames_WithOneByteInput_ReadFully()
    {
        using var source = new ShortReadStream([.. TwoGamesZstd, .. TwoGamesZstd]);
        using var zstd = new ZstdDecompressionStream(source);
        using var reader = new StreamReader(zstd);
        Assert.Equal(TwoGames + TwoGames, await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(94)]
    public void Zstd_TruncatedFrame_FailsInsteadOfCompleting(int length)
    {
        using var source = new MemoryStream(TwoGamesZstd[..length]);
        using var zstd = new ZstdDecompressionStream(source);
        Assert.Throws<InvalidDataException>(() => zstd.CopyTo(Stream.Null));
    }

    [Fact]
    public async Task Zstd_ChecksumCorruption_PropagatesAndReleasesFile()
    {
        string path = Path.Combine(_root, "corrupt.pgn.zst");
        byte[] corrupt = [.. TwoGamesZstd];
        corrupt[^1] ^= 1;
        File.WriteAllBytes(path, corrupt);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in ChessPgnDecomposer.StreamFileGamesAsync(path, default)) { }
        });
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(corrupt.Length, exclusive.Length);
    }

    [Fact]
    public void Zstd_CorruptionPermanentlyFaultsDecoder()
    {
        byte[] corrupt = [.. TwoGamesZstd];
        corrupt[^1] ^= 1;
        using var source = new MemoryStream(corrupt);
        using var decoder = new ZstdDecompressionStream(source);
        Assert.Throws<InvalidDataException>(() => decoder.CopyTo(Stream.Null));
        var repeat = Assert.Throws<InvalidDataException>(() => decoder.Read(new byte[1]));
        Assert.Contains("cannot be resumed", repeat.Message);
    }

    [Fact]
    public async Task Zstd_PayloadLargerThanTransportBuffer_IsFullyDrained()
    {
        byte[] frame = Convert.FromHexString("28b52ffd04585400001041410100fbff39c002020010410200104102001041020010410200104102001041030010411fe7f7e4");
        using var source = new MemoryStream(frame);
        using var decoder = new ZstdDecompressionStream(source);
        byte[] buffer = new byte[8191];
        long decoded = 0;
        int count;
        while ((count = await decoder.ReadAsync(buffer)) > 0)
        {
            Assert.DoesNotContain(buffer[..count], value => value != (byte)'A');
            decoded += count;
        }
        Assert.Equal(1048576, decoded);
    }

    [Fact]
    public async Task Zstd_DisposedDuringPendingInput_NeverCallsFreedDecoder()
    {
        using var source = new PendingReadStream(TwoGamesZstd);
        var decoder = new ZstdDecompressionStream(source, leaveOpen: true);
        Task<int> pending = decoder.ReadAsync(new byte[16]).AsTask();
        Assert.False(pending.IsCompleted);
        decoder.Dispose();
        source.Resume.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await pending);
    }

    [Fact]
    public async Task Zstd_CancelledEnumeration_ReleasesFile()
    {
        string path = Path.Combine(_root, "cancel.pgn.zst");
        File.WriteAllBytes(path, TwoGamesZstd);
        using var cancellation = new CancellationTokenSource();
        await using (var games = ChessPgnDecomposer.StreamFileGamesAsync(path, cancellation.Token).GetAsyncEnumerator())
        {
            Assert.True(await games.MoveNextAsync());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await games.MoveNextAsync());
        }
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(TwoGamesZstd.Length, exclusive.Length);
    }

    [Fact]
    public void Zstd_ExplicitMissingLibrary_DoesNotSubstituteSystemLibrary()
    {
        using var source = new MemoryStream(TwoGamesZstd);
        Assert.Throws<DllNotFoundException>(() => new ZstdDecompressionStream(source,
            Path.Combine(_root, "missing-native-zstd")));
    }

    [Fact]
    public void Zstd_DisposeHonorsSourceOwnership()
    {
        using var retained = new MemoryStream(TwoGamesZstd);
        using (var decoder = new ZstdDecompressionStream(retained, leaveOpen: true)) decoder.CopyTo(Stream.Null);
        Assert.True(retained.CanRead);
        var owned = new MemoryStream(TwoGamesZstd);
        using (var decoder = new ZstdDecompressionStream(owned)) decoder.CopyTo(Stream.Null);
        Assert.False(owned.CanRead);
    }

    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(1, buffer.Length)]);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class PendingReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public void OpeningsAndBooks_AlsoRefuseEmptyInput()
    {
        var dir = Dir("empty2");
        Assert.Throws<ChessInputException>(
            () => ChessInput.Resolve(dir, SearchOption.TopDirectoryOnly, ChessInput.OpeningsExtensions, "openings"));
        Assert.Throws<ChessInputException>(
            () => ChessInput.Resolve(dir, SearchOption.TopDirectoryOnly, ChessInput.BookExtensions, "chess-books"));
    }

    [Fact]
    public void SyzygyRoot_KeepsEveryMenBracketAndSeparateDtzDirectories()
    {
        var root = Dir("tablebases");
        string[] directories =
        [
            Path.Combine(root, "3-4-5", "WDL"),
            Path.Combine(root, "3-4-5", "DTZ"),
            Path.Combine(root, "6", "WDL"),
            Path.Combine(root, "6", "DTZ"),
        ];
        string[] names = ["KQvK.rtbw", "KQvK.rtbz", "KPPvKPP.rtbw", "KPPvKPP.rtbz"];
        for (int i = 0; i < directories.Length; i++)
        {
            Directory.CreateDirectory(directories[i]);
            File.WriteAllBytes(Path.Combine(directories[i], names[i]), [0]);
        }

        Assert.Equal(root, ChessInput.ResolveSyzygyPackagingDir(root));
        Assert.Equal(directories.OrderBy(static p => p, StringComparer.Ordinal),
            ChessInput.SyzygyProbePath(root).Split(Path.PathSeparator));
        var files = ChessInput.Resolve(root, SearchOption.AllDirectories,
            ChessSyzygyDecomposer.PackageExtensions, "chess-syzygy");
        Assert.Equal(4, files.Count);
        Assert.Null(ChessSyzygyDecomposer.ExplainEmptyDirectory(root, 3));
    }

    [Fact]
    public void SyzygyIndependentRootsShareOnePairedInventoryAndNativePathList()
    {
        string wdlRoot = Dir("independent-wdl"), dtzRoot = Dir("independent-dtz");
        string wdlDirectory = Path.Combine(wdlRoot, "3-4-5"), dtzDirectory = Path.Combine(dtzRoot, "nested", "3-4-5");
        Directory.CreateDirectory(wdlDirectory);
        Directory.CreateDirectory(dtzDirectory);
        string wdl = Path.Combine(wdlDirectory, "KQvK.rtbw"), dtz = Path.Combine(dtzDirectory, "KQvK.rtbz");
        string selection = string.Join(Path.PathSeparator, wdlRoot, dtzRoot);
        File.WriteAllBytes(wdl, [1]);
        Assert.False(ChessLabPaths.ResolveSyzygyDirCore(selection, _root).Found);
        Assert.Contains("missing DTZ: KQvK", Assert.Throws<ChessInputException>(
            () => ChessInput.ResolveSyzygyPackagingDir(selection)).Message);
        File.WriteAllBytes(dtz, [1]);
        Assert.Equal(new ChessLabPaths.Probe(selection, true, "config"),
            ChessLabPaths.ResolveSyzygyDirCore(selection, _root));
        Assert.Equal(selection, ChessInput.ResolveSyzygyPackagingDir(selection));
        Assert.Equal(new[] { wdl, dtz }.Order(StringComparer.Ordinal), ChessSyzygyPaths.Packages(selection));
        Assert.Equal(new[] { wdlDirectory, dtzDirectory }.Order(StringComparer.Ordinal),
            ChessInput.SyzygyProbePath(selection).Split(Path.PathSeparator));
        Assert.Equal(2, ChessSyzygyPaths.Packages(selection + Path.PathSeparator + wdlRoot).Count);

        string missing = Path.Combine(_root, "missing-selected-root");
        string missingSelection = selection + Path.PathSeparator + missing;
        Assert.False(ChessLabPaths.ResolveSyzygyDirCore(missingSelection, _root).Found);
        Assert.Contains(missing, Assert.Throws<ChessInputException>(
            () => ChessInput.SyzygyProbePath(missingSelection)).Message);
        File.WriteAllBytes(dtz, []);
        Assert.False(ChessLabPaths.ResolveSyzygyDirCore(selection, _root).Found);
        Assert.Contains("empty", Assert.Throws<ChessInputException>(
            () => ChessInput.ResolveSyzygyPackagingDir(selection)).Message);
    }

    [Fact]
    public void SyzygyExplicitNativeRejectionCannotBecomeOptionalNoOp()
    {
        foreach (int rejected in new[] { -1, 0 })
        {
            var error = Assert.Throws<ChessInputException>(() =>
                ChessSyzygyPaths.RequireNativeSelection("selected-wdl:selected-dtz", rejected, true));
            Assert.Contains("selected-wdl:selected-dtz", error.Message);
            Assert.Contains($"init={rejected}", error.Message);
            Assert.Equal(rejected, ChessSyzygyPaths.RequireNativeSelection("default", rejected, false));
        }
        Assert.Equal(3, ChessSyzygyPaths.RequireNativeSelection("selected", 3, true));
    }

    [Fact]
    public void SyzygyExplicitMissingOrEmptyRoot_IsNotReplacedByAnotherCorpus()
    {
        var empty = Dir("empty-tablebases");
        Assert.Throws<ChessInputException>(() => ChessInput.ResolveSyzygyPackagingDir(empty));
        Assert.Throws<ChessInputException>(() =>
            ChessInput.ResolveSyzygyPackagingDir(Path.Combine(_root, "missing-tablebases")));
    }

    [Theory]
    [InlineData("a.PGN", true)]
    [InlineData("a.pgn.gz", true)]
    [InlineData("a.pgn.zst", true)]
    [InlineData("a.ZST", true)]
    [InlineData("a.ZIP", true)]
    [InlineData("a.pgn.bak", false)]
    [InlineData("pgn", false)]
    public void ExtensionMatching_IsCaseInsensitiveAndSuffixExact(string name, bool expected)
        => Assert.Equal(expected, ChessInput.HasExtension(name, ChessInput.PgnExtensions));
}
