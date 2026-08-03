using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Substrate.Tests.Crud;

public sealed class InstalledOpInvokerTests
{
    [Fact]
    public void ParseSignature_NamesTypesAndOptionality()
    {
        var ps = InstalledOpInvoker.ParseSignature(
            "p_source text DEFAULT NULL::text, p_limit integer DEFAULT 24");
        Assert.Equal(2, ps.Count);
        Assert.Equal("p_source", ps[0].Name);
        Assert.Equal("text", ps[0].Type);
        Assert.True(ps[0].Optional);
        Assert.Equal("p_limit", ps[1].Name);
        Assert.Equal("integer", ps[1].Type);
        Assert.True(ps[1].Optional);
    }

    [Fact]
    public void ParseSignature_PreservesCommasInsideTypes()
    {
        var ps = InstalledOpInvoker.ParseSignature("p_v numeric(10,2), p_name text");
        Assert.Equal(2, ps.Count);
        Assert.Equal("numeric(10,2)", ps[0].Type);
        Assert.False(ps[0].Optional);
        Assert.Equal("p_name", ps[1].Name);
    }
}
