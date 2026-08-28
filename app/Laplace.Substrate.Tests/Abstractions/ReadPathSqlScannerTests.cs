using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class ReadPathSqlScannerTests
{
    [Theory]
    [InlineData("var path = @\"D:\\Data\\Laplace\"; // can select distro 14.1")]
    [InlineData("var path = @\"D:\\Data\\Laplace\"; /* SELECT * FROM ignored */")]
    [InlineData("// cmd.CommandText = \"SELECT 1\";\nvar path = @\"D:\\Data\";")]
    [InlineData("/* conn.CreateCommand(sql); */ var path = \"stockfish\";")]
    [InlineData("var path = @\"D:\\Data\\Laplace\"; var other = \"selection\";")]
    [InlineData("var text = \"CreateCommand(sql)\";")]
    [InlineData("var message = \"Use shape/bands/elaborate, or select the completions model.\";")]
    [InlineData("var text = \"\"\"path\"\"\"; // select the managed release")]
    [InlineData("var path = $@\"D:\\Data\\{name}\"; // select managed")]
    [InlineData("#if OTHER_PLATFORM\n// cmd.CommandText = sql;\n#endif")]
    public void IgnoresCommentsAndDoesNotCrossStringBoundaries(string source)
        => Assert.False(ReadPathArchitectureGateTests.HasHandWrittenSql(source));

    [Theory]
    [InlineData("var sql = \"SELECT * FROM entities\";")]
    [InlineData("var sql = @\"WITH x AS (SELECT * FROM entities) SELECT * FROM x\";")]
    [InlineData("var sql = \"\"\"SELECT * FROM entities\"\"\";")]
    [InlineData("var sql = \"\"\"\nSELECT * FROM entities\n\"\"\";")]
    [InlineData("var sql = \"\"\"\"SELECT 'a triple quote: \\\"\\\"\\\"'\"\"\"\";")]
    [InlineData("var sql = $\"SELECT * FROM {table}\";")]
    [InlineData("var sql = $@\"WITH x AS (SELECT * FROM {table}) SELECT * FROM x\";")]
    [InlineData("var sql = $$\"\"\"SELECT * FROM {{table}}\"\"\";")]
    [InlineData("var sql = @\"note \"\"quoted\"\" SELECT * FROM entities\";")]
    [InlineData("var sql = \"\\u0053ELECT * FROM entities\";")]
    [InlineData("var sql = \"SELECT * FROM entities\"u8;")]
    [InlineData("cmd.CommandText = sql;")]
    [InlineData("var cmd = conn.CreateCommand(sql);")]
    [InlineData("var cmd = conn?.CreateCommand(sql);")]
    [InlineData("var cmd = CreateCommand(sql);")]
    [InlineData("#if OTHER_PLATFORM\nvar sql = \"SELECT 1\";\n#endif")]
    public void StillDetectsSqlAndCommandConstruction(string source)
        => Assert.True(ReadPathArchitectureGateTests.HasHandWrittenSql(source));

    [Fact]
    public void CommentThenRealSqlDoesNotHideTheViolation()
    {
        const string comment = "var path = @\"D:\\Data\"; // select distro\n";
        Assert.False(ReadPathArchitectureGateTests.HasHandWrittenSql(comment));
        Assert.True(ReadPathArchitectureGateTests.HasHandWrittenSql(comment + "var sql = @\"SELECT 1\";"));
    }
}
