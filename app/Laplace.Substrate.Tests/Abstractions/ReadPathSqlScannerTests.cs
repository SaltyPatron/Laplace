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
    [InlineData("var message = $\"'{source}' declares multiple roots; select a path explicitly.\";")]
    public void IgnoresCommentsAndDoesNotCrossStringBoundaries(string source)
        => Assert.False(ReadPathArchitectureGateTests.HasHandWrittenSql(source));

    [Theory]
    [InlineData("var sql = \"SELECT * FROM entities\";")]
    [InlineData("var sql = @\"WITH x AS (SELECT * FROM entities) SELECT * FROM x\";")]
    [InlineData("var sql = \"\"\"SELECT * FROM entities\"\"\";")]
    [InlineData("var sql = \"\"\"\nSELECT * FROM entities\n\"\"\";")]
    [InlineData("var sql = \"\"\"\"SELECT 'a triple quote: \\\"\\\"\\\"'\"\"\"\";")]
    [InlineData("var sql = $\"SELECT * FROM {table}\";")]
    [InlineData("var sql = $\"WITH x AS (SELECT * FROM {table}) SELECT * FROM x\";")]
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

    [Fact]
    public void StorageSqlScanChecksLiteralsWithoutTreatingCatalogCommandCreationAsSql()
    {
        const string catalog = "var command = NpgsqlRead.CreateCommand(source, SqlCatalog.Get(\"entity.facets\"));";
        Assert.False(ReadPathArchitectureGateTests.HasInlineSql(catalog));
        Assert.True(ReadPathArchitectureGateTests.HasInlineSql(catalog + "var sql = \"SELECT 1\";"));
        Assert.True(ReadPathArchitectureGateTests.HasInlineSql("#if OTHER_PLATFORM\nvar sql = \"SELECT 1\";\n#endif"));
    }

    [Theory]
    [InlineData("var sql = \"INSERT INTO t VALUES($1)\";")]
    [InlineData("var sql = \"UPDATE t SET n=$1\";")]
    [InlineData("var sql = \"DELETE FROM t\";")]
    [InlineData("var sql = $\"COPY {table} ({columns}) FROM STDIN (FORMAT BINARY)\";")]
    [InlineData("var sql = \"CREATE TABLE t(n integer)\";")]
    [InlineData("var sql = \"SET LOCAL jit = off\";")]
    [InlineData("#if OTHER_PLATFORM\nvar sql = \"DROP SCHEMA x CASCADE\";\n#endif")]
    public void StorageScanIncludesWritesUtilitiesAndCopy(string source)
        => Assert.True(ReadPathArchitectureGateTests.HasInlineSql(source));

    [Theory]
    [InlineData("throw new ArgumentException(\"Select at least one catalog command.\");")]
    [InlineData("log.LogWarning(\"SELECT pg_terminate_backend({Pid}) frees this ingest\", pid);")]
    public void DiagnosticArgumentsAreNotExecutedStatements(string source)
        => Assert.False(ReadPathArchitectureGateTests.HasInlineSql(source));

    [Fact]
    public void DiagnosticDoesNotHideACommandConstructedInsideItsArgument()
        => Assert.True(ReadPathArchitectureGateTests.HasInlineSql(
            "log.LogWarning(GetMessage(new NpgsqlCommand(\"SELECT 1\", connection)));") );
}
