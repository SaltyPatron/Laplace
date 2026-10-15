using Laplace.Engine.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class TurnWitnessReceiptTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task EmptyResponseDoesNotClaimACommittedWitness(string? reply)
    {
        using var witness = new TurnWitness(null!, NullLogger<TurnWitness>.Instance);
        Assert.False(await witness.RecordResponseAsync(
            "test", null, Hash128.Zero, "prompt", reply, "occurrence", CancellationToken.None));
    }
}
