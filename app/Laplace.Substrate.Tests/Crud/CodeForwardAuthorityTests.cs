using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class CodeForwardAuthorityTests
{
    [Fact]
    public void AdapterLeavesKnowledgeAndProviderSelectionToCanonicalForwardProgram()
    {
        string sql = NpgsqlSubstrateReads.ForwardCodeSql;

        Assert.Contains("generation.forward_text(", sql, StringComparison.Ordinal);
        Assert.Contains("$6::bytea[]", sql, StringComparison.Ordinal);

        // A product adapter may carry prior witnessed state, but it must not
        // preselect a conventional-model continuation plane or a private code
        // evidence subset before the canonical COUPLE/ORIENT/ROUTE program runs.
        Assert.DoesNotContain("COMPLETES_TO", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("generation.adjudicated_row", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DEFINES", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CALLS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("cardinality", sql, StringComparison.Ordinal);
    }
}
