using Xunit;
using Laplace.Decomposers.Abstractions.Tests;

namespace Laplace.Ingestion.Tests;

/// <summary>I3: turn witness must expose availability and fail closed when offline.</summary>
public sealed class TurnWitnessIntegrationTests
{
    [Fact]
    public void TurnWitness_ExposesAvailabilityAndRecordOrFailApi()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var witness = Path.Combine(repoRoot, "app", "Laplace.Endpoints.OpenAICompat", "TurnWitness.cs");
        var endpoints = Path.Combine(repoRoot, "app", "Laplace.Endpoints.OpenAICompat", "EndpointMappings.Inference.cs");
        var witnessText = File.ReadAllText(witness);
        var endpointsText = File.ReadAllText(endpoints);
        Assert.Contains("IsAvailable", witnessText, StringComparison.Ordinal);
        Assert.Contains("TryEnqueue", witnessText, StringComparison.Ordinal);
        Assert.DoesNotContain("DropWrite", witnessText, StringComparison.Ordinal);
        Assert.Contains("RequireTurnWitness", endpointsText, StringComparison.Ordinal);
        Assert.Contains("witness_unavailable", endpointsText, StringComparison.Ordinal);
    }
}
