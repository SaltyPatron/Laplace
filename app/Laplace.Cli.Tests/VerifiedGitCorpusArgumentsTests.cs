using Xunit;

namespace Laplace.Cli.Tests;

public sealed class VerifiedGitCorpusArgumentsTests
{
    private static string[] ValidArguments => ["repo", "/fixture/source", "--git-corpus-selection", "/fixture/selection.json",
        "--git-corpus-receipt", "/fixture/receipt.json"];

    [Theory]
    [InlineData("--force")]
    [InlineData("--no-evidence")]
    [InlineData("--register-only")]
    [InlineData("--recursive")]
    public async Task DirectAndChainDispatchRejectReobservationBeforeRuntimeLoad(string flag)
    {
        string[] args = [.. ValidArguments, flag];
        Assert.Throws<ArgumentException>(() => IngestCommands.ParseIngestCliArgs(args));
        await Assert.ThrowsAsync<ArgumentException>(() => IngestCommands.IngestAsync(args));
        await Assert.ThrowsAsync<ArgumentException>(() => IngestCommands.IngestAsync(["chain", string.Join(' ', args)]));
    }

    [Fact]
    public void OrdinaryRepoArgumentsKeepExistingMeaning()
    {
        var ordinary = IngestCommands.ParseIngestCliArgs(["repo", "/fixture/source", "--force"]);
        Assert.True(ordinary.Force);
        Assert.Null(ordinary.GitCorpusSelection);
        Assert.Null(ordinary.GitCorpusReceipt);
    }

    [Fact]
    public void VerifiedPathsAreConsumedAndDoNotBecomeASecondInput()
    {
        var parsed = IngestCommands.ParseIngestCliArgs(ValidArguments);
        Assert.Equal("/fixture/source", parsed.Path);
        Assert.Equal("", parsed.SecondPath);
        Assert.Equal("/fixture/selection.json", parsed.GitCorpusSelection);
        Assert.Equal("/fixture/receipt.json", parsed.GitCorpusReceipt);
    }

    [Fact]
    public async Task DirectRepoEntryCannotBypassSharedValidation()
    {
        var parsed = IngestCommands.ParseIngestCliArgs(ValidArguments) with { Force = true };
        await Assert.ThrowsAsync<ArgumentException>(() => IngestCommands.IngestRepoAsync(parsed));
    }
}
