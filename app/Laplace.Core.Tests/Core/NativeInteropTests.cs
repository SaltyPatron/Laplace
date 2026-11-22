using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

public class NativeInteropTests
{
    [Fact]
    public void NativeIdentity_ResolvesCodepointAndTextDeterministically()
    {
        var cpA = Hash128.Codepoint('A');
        Assert.Equal("32684bfa28c0c84d6f210511aace0efc", cpA.ToHex());
        Assert.Equal(cpA, Hash128.Text("A"));
    }
}
