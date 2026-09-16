using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class OpCatalogProjectionTests
{
    [Theory]
    [InlineData("ops.evict_source", true, true)]
    [InlineData("ops.cancel_backend", true, false)]
    [InlineData("ops.looks_like_reset", false, false)]
    public void UsesTheInvokerPolicyNotOperationNameHeuristics(string name, bool writable, bool destructive)
    {
        var description = OpCatalogProjection.Describe(Row(name, "p_id bytea, p_note text DEFAULT 'a,b'::text"));
        Assert.Equal(writable, description.Writable);
        Assert.Equal(destructive, description.Destructive);
        Assert.Collection(description.Parameters,
            p => { Assert.Equal("p_id", p.Name); Assert.Equal("bytea", p.Type); Assert.False(p.Optional); },
            p => { Assert.Equal("p_note", p.Name); Assert.Equal("text", p.Type); Assert.True(p.Optional); });
    }

    [Fact]
    public void KeepsTheExactSignatureAndEveryInvokerParameter()
    {
        const string signature = "p_names text[], p_limit bigint DEFAULT 9223372036854775807";
        var result = OpCatalogProjection.Describe(Row("ops.example", signature));
        Assert.Equal(signature, result.Args);
        Assert.Equal(InstalledOpInvoker.ParseSignature(signature).Select(p => (p.Name, p.Type, p.Optional)),
            result.Parameters.Select(p => (p.Name, p.Type, p.Optional)));
    }

    [Fact]
    public void MissingCatalogFieldsDoNotBecomeAUsableEmptySignature()
    {
        Assert.Throws<InvalidDataException>(() => OpCatalogProjection.Describe(new Dictionary<string, object?> { ["name"] = "ops.example" }));
    }

    [Fact]
    public void ZeroParametersAndProcedureReturnMeaningRemainDistinct()
    {
        var row = Row("ops.analyze_substrate", ""); row["kind"] = "procedure"; row["returns"] = null;
        var result = OpCatalogProjection.Describe(row);
        Assert.Empty(result.Parameters); Assert.Null(result.Returns); Assert.Equal("procedure", result.Kind);
    }

    private static Dictionary<string, object?> Row(string name, string args) => new(StringComparer.Ordinal)
    { ["name"] = name, ["args"] = args, ["returns"] = "TABLE(value text)", ["kind"] = "function" };
}
