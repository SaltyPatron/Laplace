using System.Text.Json.Nodes;
using Laplace.SubstrateCRUD.Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.Substrate.Tests.Crud;

public sealed class InstalledOpInvokerTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(200, 200)]
    [InlineData(2001, 2001)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void RequestedRowCount_HonorsTheCallerWithoutAnUpperCeiling(int requested, int expected)
    {
        Assert.Equal(expected, InstalledOpInvoker.RequestedRowCount(requested));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(200, 200)]
    [InlineData(201, 201)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void RequestedReadLimit_HonorsTheCallerWithoutAnUpperCeiling(int requested, int expected)
    {
        Assert.Equal(expected, NpgsqlSubstrateReads.RequestedLimit(requested));
    }

    [Theory]
    [InlineData("substrate_health", "laplace.\"substrate_health\"")]
    [InlineData("ops.substrate_counts", "\"ops\".\"substrate_counts\"")]
    [InlineData("converse.resolve_topic", "\"converse\".\"resolve_topic\"")]
    [InlineData("public.laplace_hash128_blake3", "\"public\".\"laplace_hash128_blake3\"")]
    public void QualifiedCatalogName_QuotesSchemaAndFunctionSeparately(string catalogName, string expected)
    {
        Assert.Equal(expected, InstalledOpInvoker.QualifiedCatalogName(catalogName));
    }

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

    // GH #843 alleged that the splitter's quote toggle mis-handles a doubled quote
    // and splits `DEFAULT 'a''b, c'` at the inner comma. It does not — the pair
    // toggles twice with no character in between, so the comma is still seen as
    // quoted. Brute force over every string of length <= 7 drawn from {' , ( ) a}
    // found no input where the toggle and an explicit escape arm differ. These two
    // tests pin that, so the next reviewer to flag it has an answer.
    [Fact]
    public void ParseSignature_DoubledQuoteInDefaultDoesNotSplitTheParameter()
    {
        var ps = InstalledOpInvoker.ParseSignature(
            "p_tag text DEFAULT 'a''b, c'::text, p_limit integer DEFAULT 24");
        Assert.Equal(2, ps.Count);
        Assert.Equal("p_tag", ps[0].Name);
        Assert.Equal("text", ps[0].Type);
        Assert.True(ps[0].Optional);
        Assert.Equal("p_limit", ps[1].Name);
        Assert.Equal("integer", ps[1].Type);
    }

    [Fact]
    public void ParseSignature_ConsecutiveEscapedQuotesStayInsideTheLiteral()
    {
        var ps = InstalledOpInvoker.ParseSignature(
            "p_a text DEFAULT '''', p_b text DEFAULT 'x,y', p_c integer");
        Assert.Equal(3, ps.Count);
        Assert.Equal("p_a", ps[0].Name);
        Assert.Equal("p_b", ps[1].Name);
        Assert.Equal("p_c", ps[2].Name);
        Assert.Equal("integer", ps[2].Type);
    }

    // --- Array binding (GH #843) ------------------------------------------------
    // Every case below silently mis-parsed while the array was composed as the
    // literal "{" + join(",") + "}". They assert the value that reaches Npgsql,
    // which is where the quoting question stops existing.

    private static string?[] Elements(string json) =>
        Assert.IsType<string?[]>(InstalledOpInvoker.OpValue(JsonNode.Parse(json)));

    [Fact]
    public void OpValue_ElementWithComma_StaysOneElement()
    {
        var items = Elements("""["a,b", "c"]""");
        Assert.Equal(2, items.Length);
        Assert.Equal("a,b", items[0]);
        Assert.Equal("c", items[1]);
    }

    [Fact]
    public void OpValue_ElementWithQuotesAndBackslash_PassesThroughUnescaped()
    {
        var items = Elements("""["it's \"quoted\"", "back\\slash"]""");
        Assert.Equal(2, items.Length);
        Assert.Equal("it's \"quoted\"", items[0]);
        Assert.Equal(@"back\slash", items[1]);
    }

    [Fact]
    public void OpValue_ElementWithBraces_StaysOneElement()
    {
        var items = Elements("""["{x}", "}", "{"]""");
        Assert.Equal(3, items.Length);
        Assert.Equal("{x}", items[0]);
        Assert.Equal("}", items[1]);
        Assert.Equal("{", items[2]);
    }

    [Fact]
    public void OpValue_ElementWithEdgeWhitespace_KeepsIt()
    {
        var items = Elements("""["  padded  ", ""]""");
        Assert.Equal(2, items.Length);
        Assert.Equal("  padded  ", items[0]);
        Assert.Equal(string.Empty, items[1]);
    }

    [Fact]
    public void OpValue_EmptyArray_IsAnEmptyArrayNotAnEmptyLiteral()
    {
        var items = Elements("[]");
        Assert.Empty(items);
    }

    [Fact]
    public void OpValue_JsonNullElement_IsSqlNullAndDistinctFromTheStringNull()
    {
        var items = Elements("""[null, "NULL"]""");
        Assert.Equal(2, items.Length);
        Assert.Null(items[0]);
        Assert.Equal("NULL", items[1]);
    }

    [Fact]
    public void OpValue_NonStringElements_RenderAsText()
    {
        var items = Elements("""[1, 2.5, true]""");
        Assert.Equal(3, items.Length);
        Assert.Equal("1", items[0]);
        Assert.Equal("2.5", items[1]);
        Assert.Equal("true", items[2]);
    }

    [Fact]
    public void OpValue_ScalarsAreUnchanged()
    {
        Assert.Null(InstalledOpInvoker.OpValue(null));
        Assert.Equal("plain", InstalledOpInvoker.OpValue(JsonValue.Create("plain")));
        Assert.Equal("42", InstalledOpInvoker.OpValue(JsonNode.Parse("42")));
    }

    [Fact]
    public void BindArg_Array_BindsAsTypedTextArray()
    {
        var p = InstalledOpInvoker.BindArg("a0", InstalledOpInvoker.OpValue(JsonNode.Parse("""["a,b", null]""")));
        Assert.Equal(NpgsqlDbType.Array | NpgsqlDbType.Text, p.NpgsqlDbType);
        var items = Assert.IsType<string?[]>(p.Value);
        Assert.Equal("a,b", items[0]);
        Assert.Null(items[1]);
    }

    [Fact]
    public void BindArg_Null_IsTypedTextDbNull()
    {
        var p = InstalledOpInvoker.BindArg("a0", null);
        Assert.Equal(NpgsqlDbType.Text, p.NpgsqlDbType);
        Assert.Equal(DBNull.Value, p.Value);
    }

    [Fact]
    public void BindArg_Scalar_KeepsTheValue()
    {
        var p = InstalledOpInvoker.BindArg("a0", "plain");
        Assert.Equal("plain", p.Value);
    }
}
