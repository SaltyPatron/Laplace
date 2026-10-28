using Xunit;

namespace Laplace.Cli.Tests;

[CollectionDefinition("Ingest chain console", DisableParallelization = true)]
public sealed class IngestChainConsoleCollection;

[Collection("Ingest chain console")]
public sealed class IngestChainPreflightTests
{
    [Theory]
    [InlineData("owen", "unknown source 'owen' in spec 2")]
    [InlineData(" ", "spec 2 is empty")]
    public async Task InvalidLaterSpecStartsNoEarlierSource(string spec, string diagnostic)
    {
        // A dispatched first command would return its own snapshot validation
        // error. No database/native runtime is needed to prove that the entire
        // chain is checked before its first handler is invoked.
        var output = new StringWriter();
        var error = new StringWriter();
        var previousOutput = Console.Out;
        var previousError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            int result = await IngestCommands.IngestAsync(["chain", "safetensors /missing-snapshot", spec]);
            Assert.Equal(2, result);
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }
        Assert.Contains(diagnostic, error.ToString());
        Assert.Contains("no sources started", error.ToString());
        Assert.DoesNotContain("==== chain [", output.ToString());
    }

    [Theory]
    [InlineData("unicode")]
    [InlineData("iso639")]
    [InlineData("oewn")]
    [InlineData("open-english-wordnet")]
    [InlineData("OMW")]
    [InlineData("safetensors")]
    public void PreflightAcceptsDispatchNamesAndAliases(string source)
        => Assert.True(IngestDispatchTable.IsRegistered(source));
}
