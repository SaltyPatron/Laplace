using Laplace.Decomposers.Code;
using Xunit;

namespace Laplace.Decomposers.Code.Tests;

public sealed class CodeDecomposerTests
{
    [Theory]
    [InlineData("query.sql", "sql")]
    [InlineData("query.sql.in", "sql")]
    [InlineData("QUERY.SQL.IN", "sql")]
    public void ModalityOf_ResolvesSqlAndTemplatedSql(string path, string expected)
        => Assert.Equal(expected, CodeDecomposer.ModalityOf(path));

    [Theory]
    [InlineData("unknown.in")]
    [InlineData("unknown")]
    [InlineData("query.sql.in.in")]
    public void ModalityOf_DoesNotTreatTemplateSuffixAsSql(string path)
        => Assert.Null(CodeDecomposer.ModalityOf(path));
}
