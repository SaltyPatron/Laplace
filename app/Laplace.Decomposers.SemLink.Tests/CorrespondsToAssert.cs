using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.SemLink.Tests;

internal static class CorrespondsToAssert
{
    internal static void Contains(
        IEnumerable<AttestationRow> attestations, Hash128 categoryId, Hash128 synsetId)
    {
        var corr = RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO");
        bool match(AttestationRow a) =>
            a.TypeId == corr &&
            ((a.SubjectId == categoryId && a.ObjectId == synsetId) ||
             (a.SubjectId == synsetId && a.ObjectId == categoryId));
        Assert.Contains(attestations, match);
    }
}
