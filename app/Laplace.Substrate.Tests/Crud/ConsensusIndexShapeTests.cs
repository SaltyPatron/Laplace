using Laplace.Decomposers.Abstractions.Tests;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class ConsensusIndexShapeTests
{
    [Fact]
    public void SubjectTypeIndex_CarriesExactConsensusCellId()
    {
        var root = TypeIdLawTests.FindRepoRootPublic();
        var sql = File.ReadAllText(Path.Combine(
            root, "extension", "laplace_substrate", "sql", "indexes",
            "consensus_subject_type_btree.sql.in"));

        Assert.Contains(
            "ON consensus (subject_id, type_id, id)", sql,
            StringComparison.OrdinalIgnoreCase);
    }
}
