using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests.Core;

public class StagedClaimIdentityTests
{
    [Theory]
    [InlineData(false, false, "e528e95798037df410543d9f31e396ec")]
    [InlineData(true, false, "5e9902524b30f4f4cfd5d2180fd357bb")]
    [InlineData(true, true, "bb7fea3858e878658da23d1ed4bd8236")]
    public void ClaimIdentityPreservesActualArity(bool hasObject, bool zeroObject, string expected)
    {
        Hash128 subject = Hash128.FromBytes(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        Hash128 relation = Hash128.FromBytes(Enumerable.Range(16, 16).Select(i => (byte)i).ToArray());
        Hash128? obj = hasObject
            ? zeroObject ? Hash128.Zero : Hash128.FromBytes(Enumerable.Range(32, 16).Select(i => (byte)i).ToArray())
            : null;
        Assert.Equal(Convert.FromHexString(expected), ConsensusKeys.EdgeId(subject, relation, obj).ToBytes());
        if (!hasObject)
            Assert.NotEqual(ConsensusKeys.EdgeId(subject, relation, Hash128.Zero),
                ConsensusKeys.EdgeId(subject, relation, obj));
    }
}
