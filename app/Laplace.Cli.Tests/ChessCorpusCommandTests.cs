using Xunit;

namespace Laplace.Cli.Tests;

public sealed class ChessCorpusCommandTests
{
    private static string Root => Path.GetPathRoot(Path.GetTempPath())!;
    private static string Pgn => Path.Combine(Root, "laplace-corpus-argument-fixture", "games.pgn");
    private static string Evidence => Path.Combine(Root, "laplace-corpus-argument-fixture", "evidence");
    private static string[] Required => ["--pgn", Pgn, "--evidence-root", Evidence];

    [Fact]
    public void ExplicitPathsUseDeclaredDefaultsWithoutOpeningFiles()
    {
        var options = ChessCorpusCommands.ParseArguments(Required);
        Assert.Equal(Pgn, options.PgnPath);
        Assert.Equal(Evidence, options.EvidenceDirectory);
        Assert.Equal(75_000, options.Games);
        Assert.Equal(30d, options.MinimumSeconds);
        Assert.Equal(1, options.Replays);
        Assert.Equal(3600, options.DeadlineSeconds);
        Assert.Null(options.ExpectedSha256);
    }

    [Fact]
    public void EveryOptionIsRetainedAndFlagOrderIsIndependent()
    {
        string digest = new('a', 64);
        var options = ChessCorpusCommands.ParseArguments([
            "--expected-sha256", digest, "--deadline-seconds", "86400", "--replays", "4",
            "--pgn", Pgn, "--games", "1000000", "--evidence-root", Evidence, "--minimum-seconds", "3600"]);
        Assert.Equal(digest, options.ExpectedSha256);
        Assert.Equal(1_000_000, options.Games);
        Assert.Equal(3600d, options.MinimumSeconds);
        Assert.Equal(4, options.Replays);
        Assert.Equal(86_400, options.DeadlineSeconds);
    }

    [Fact]
    public void LowerBoundsAndFractionalSecondsAreAccepted()
    {
        var options = ChessCorpusCommands.ParseArguments([
            .. Required, "--games", "1", "--replays", "1", "--minimum-seconds", "30.5",
            "--deadline-seconds", "31"]);
        Assert.Equal(1, options.Games);
        Assert.Equal(30.5, options.MinimumSeconds);
        Assert.Equal(31, options.DeadlineSeconds);
        Assert.Equal(30, ChessCorpusCommands.ParseArguments([
            .. Required, "--minimum-seconds", "30", "--deadline-seconds", "30"]).DeadlineSeconds);
    }

    [Theory]
    [InlineData("--pgn")]
    [InlineData("--evidence-root")]
    [InlineData("--games")]
    [InlineData("--minimum-seconds")]
    [InlineData("--replays")]
    [InlineData("--deadline-seconds")]
    [InlineData("--expected-sha256")]
    public void EveryFlagRejectsDuplicates(string flag)
    {
        string value = flag switch
        {
            "--pgn" => Pgn,
            "--evidence-root" => Evidence,
            "--games" => "10",
            "--minimum-seconds" => "30",
            "--replays" => "1",
            "--deadline-seconds" => "3600",
            _ => new string('a', 64),
        };
        string[] args = flag is "--pgn" or "--evidence-root"
            ? [.. Required, flag, value]
            : [.. Required, flag, value, flag, value];
        Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments(args));
    }

    [Theory]
    [InlineData("--pgn")]
    [InlineData("--evidence-root")]
    [InlineData("--games")]
    [InlineData("--minimum-seconds")]
    [InlineData("--replays")]
    [InlineData("--deadline-seconds")]
    [InlineData("--expected-sha256")]
    public void EveryFlagRejectsMissingAndEmptyValues(string flag)
    {
        Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments([.. Required, flag]));
        Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments([.. Required, flag, ""]));
        Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments([.. Required, flag, "   "]));
        Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments([.. Required, flag, "--games", "10"]));
    }

    [Theory]
    [InlineData("--unknown", "value")]
    [InlineData("--force", "true")]
    [InlineData("--no-evidence", "true")]
    [InlineData("--recursive", "true")]
    [InlineData("--no-analyze", "true")]
    [InlineData("--Games", "20")]
    [InlineData("trailing.pgn", "extra")]
    public void UnknownFlagsAndTrailingPositionalsAreRejected(string first, string second)
    {
        Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments([.. Required, first, second]));
        Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments([.. Required, first]));
    }

    [Theory]
    [InlineData("--games", "0")]
    [InlineData("--games", "1000001")]
    [InlineData("--games", "-1")]
    [InlineData("--games", "+1")]
    [InlineData("--games", "1.5")]
    [InlineData("--games", "1e3")]
    [InlineData("--games", "2147483648")]
    [InlineData("--games", " 10")]
    [InlineData("--games", "ten")]
    [InlineData("--replays", "0")]
    [InlineData("--replays", "5")]
    [InlineData("--deadline-seconds", "29")]
    [InlineData("--deadline-seconds", "86401")]
    [InlineData("--minimum-seconds", "29.999")]
    [InlineData("--minimum-seconds", "3600.001")]
    [InlineData("--minimum-seconds", "NaN")]
    [InlineData("--minimum-seconds", "Infinity")]
    [InlineData("--minimum-seconds", "-Infinity")]
    [InlineData("--minimum-seconds", "1e999")]
    [InlineData("--minimum-seconds", "30,5")]
    [InlineData("--minimum-seconds", " 30")]
    public void InvalidNumericValuesCannotBecomeDefaults(string flag, string value)
        => Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments([.. Required, flag, value]));

    [Fact]
    public void MinimumWindowCannotExceedDeadline()
        => Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments([
            .. Required, "--minimum-seconds", "31", "--deadline-seconds", "30"]));

    [Theory]
    [InlineData("a", 63)]
    [InlineData("a", 65)]
    [InlineData("A", 64)]
    [InlineData("g", 64)]
    [InlineData(" ", 64)]
    public void DigestRequiresExactLowercaseHexadecimal(string character, int length)
        => Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments([
            .. Required, "--expected-sha256", new string(character[0], length)]));

    [Fact]
    public void BothPathsAreRequiredAndMustBeAbsolute()
    {
        Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments([]));
        Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments(["--pgn", Pgn]));
        Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments(["--evidence-root", Evidence]));
        Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments([
            "--pgn", "games.pgn", "--evidence-root", Evidence]));
        Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments([
            "--pgn", Pgn, "--evidence-root", "evidence"]));
    }

    [Theory]
    [InlineData("games.pgn.zst")]
    [InlineData("games.pgn.gz")]
    [InlineData("games.zip")]
    [InlineData("games.txt")]
    [InlineData("games")]
    public void CompressedAndNonPgnInputsAreExplicitlyRejected(string filename)
        => Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments([
            "--pgn", Path.Combine(Root, filename), "--evidence-root", Evidence]));

    [Fact]
    public void PathsAreNormalizedBeforeComparingInputAndOutput()
    {
        string same = Path.Combine(Path.GetDirectoryName(Pgn)!, "child", "..", "games.pgn");
        Assert.Throws<ArgumentException>(() => ChessCorpusCommands.ParseArguments([
            "--pgn", Pgn, "--evidence-root", same]));
        var options = ChessCorpusCommands.ParseArguments([
            "--pgn", Path.ChangeExtension(Pgn, ".PGN"), "--evidence-root", Evidence + Path.DirectorySeparatorChar]);
        Assert.Equal(".PGN", Path.GetExtension(options.PgnPath));
        Assert.Equal(Evidence, options.EvidenceDirectory);
    }
}
