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

        // The code surface may carry prior witnessed state, but it does not preselect a
        // continuation plane or a code-only evidence subset ahead of COUPLE/ORIENT/ROUTE.
        Assert.DoesNotContain("COMPLETES_TO", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("generation.adjudicated_row", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DEFINES", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CALLS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("cardinality", sql, StringComparison.Ordinal);
    }
}
