using System.IO.Compression;
using System.Text;
using Laplace.Chess.Service;
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

    [Fact]
    public void OpeningsAndBooks_AlsoRefuseEmptyInput()
    {
        var dir = Dir("empty2");
        Assert.Throws<ChessInputException>(
            () => ChessInput.Resolve(dir, SearchOption.TopDirectoryOnly, ChessInput.OpeningsExtensions, "openings"));
        Assert.Throws<ChessInputException>(
            () => ChessInput.Resolve(dir, SearchOption.TopDirectoryOnly, ChessInput.BookExtensions, "chess-books"));
    }

    [Theory]
    [InlineData("a.PGN", true)]
    [InlineData("a.pgn.gz", true)]
    [InlineData("a.ZIP", true)]
    [InlineData("a.pgn.bak", false)]
    [InlineData("pgn", false)]
    public void ExtensionMatching_IsCaseInsensitiveAndSuffixExact(string name, bool expected)
        => Assert.Equal(expected, ChessInput.HasExtension(name, ChessInput.PgnExtensions));
}
