using Laplace.Api.Contracts;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class QueryCapacityContractTests
{
    [Fact]
    public void QueryDials_PreserveCallerCapacityAboveLegacyCeilings()
    {
        var request = new QueryRequest(
            Topic: "whale",
            Depth: 97,
            Breadth: 113,
            Steps: 701,
            MaxStride: 89,
            Limit: 1203);

        var dials = QueryDials.From(request);

        Assert.Equal(97, dials.Depth);
        Assert.Equal(113, dials.Breadth);
        Assert.Equal(701, dials.Steps);
        Assert.Equal(89, dials.MaxStride);
        Assert.Equal(1203, dials.Limit);
    }

    [Fact]
    public void QueryDials_PreserveZeroInsteadOfPromotingWork()
    {
        var dials = QueryDials.From(new QueryRequest(
            Topic: "whale", Depth: 0, Breadth: 0, Steps: 0, MaxStride: 0, Limit: 0));

        Assert.Equal(0, dials.Depth);
        Assert.Equal(0, dials.Breadth);
        Assert.Equal(0, dials.Steps);
        Assert.Equal(0, dials.MaxStride);
        Assert.Equal(0, dials.Limit);
    }
}
