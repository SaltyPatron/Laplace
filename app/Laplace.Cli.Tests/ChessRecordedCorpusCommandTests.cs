using Xunit;

namespace Laplace.Cli.Tests;

public sealed class ChessRecordedCorpusCommandTests
{
    private static string Root => Path.GetPathRoot(Path.GetTempPath())!;
    private static string Manifest => Path.Combine(Root, "recorded-selection-fixture", "selection.json");
    private static string Evidence => Path.Combine(Root, "recorded-selection-fixture", "evidence");
    private static string Digest => new('a', 64);
    private static string[] Required => ["--selection-manifest", Manifest, "--expected-sha256", Digest, "--evidence-root", Evidence];

    [Fact]
    public void ExactManifestIdentityIsRequiredAndRetainedWithoutOpeningFiles()
    {
        var options = ChessRecordedCorpusCommands.ParseArguments(Required);
        Assert.Equal(Manifest, options.ManifestPath);
        Assert.Equal(Digest, options.ExpectedSha256);
        Assert.Equal(Evidence, options.EvidenceDirectory);
        Assert.Equal(3600, options.DeadlineSeconds);
        Assert.Equal(30, ChessRecordedCorpusCommands.ParseArguments([.. Required, "--deadline-seconds", "30"]).DeadlineSeconds);
    }

    [Theory]
    [InlineData("--selection-manifest")]
    [InlineData("--expected-sha256")]
    [InlineData("--evidence-root")]
    [InlineData("--deadline-seconds")]
    public void MissingValuesAndDuplicatesAreRefused(string flag)
    {
        Assert.Throws<ArgumentException>(() => ChessRecordedCorpusCommands.ParseArguments([.. Required, flag]));
        Assert.Throws<ArgumentException>(() => ChessRecordedCorpusCommands.ParseArguments([.. Required, flag, ""]));
        string value = flag == "--deadline-seconds" ? "30" : "duplicate";
        Assert.Throws<ArgumentException>(() => ChessRecordedCorpusCommands.ParseArguments([.. Required, flag, value, flag, value]));
    }

    [Theory]
    [InlineData("--games", "140")]
    [InlineData("--force", "true")]
    [InlineData("--deadline-seconds", "29")]
    [InlineData("--deadline-seconds", "86401")]
    [InlineData("--deadline-seconds", "-1")]
    [InlineData("--deadline-seconds", "1e3")]
    public void VerificationCannotChangeSelectionOrRelaxProof(string flag, string value)
        => Assert.Throws<ArgumentException>(() => ChessRecordedCorpusCommands.ParseArguments([.. Required, flag, value]));

    [Fact]
    public void RequiredPathsDigestAndDistinctEvidenceAreValidated()
    {
        Assert.Throws<ArgumentException>(() => ChessRecordedCorpusCommands.ParseArguments([]));
        Assert.Throws<ArgumentException>(() => ChessRecordedCorpusCommands.ParseArguments([
            "--selection-manifest", "selection.json", "--expected-sha256", Digest, "--evidence-root", Evidence]));
        Assert.Throws<ArgumentException>(() => ChessRecordedCorpusCommands.ParseArguments([
            "--selection-manifest", Manifest, "--expected-sha256", Digest, "--evidence-root", "evidence"]));
        Assert.Throws<ArgumentException>(() => ChessRecordedCorpusCommands.ParseArguments([
            "--selection-manifest", Manifest, "--expected-sha256", Digest, "--evidence-root", Manifest]));
        foreach (string bad in new[] { new string('a', 63), new string('A', 64), new string('g', 64) })
            Assert.Throws<ArgumentException>(() => ChessRecordedCorpusCommands.ParseArguments([
                "--selection-manifest", Manifest, "--expected-sha256", bad, "--evidence-root", Evidence]));
    }
}
