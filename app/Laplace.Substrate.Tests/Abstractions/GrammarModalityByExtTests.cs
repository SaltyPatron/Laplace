using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;



public class GrammarModalityByExtTests
{
    [Theory]
    [InlineData("py", "python")]
    [InlineData("rs", "rust")]
    [InlineData("cs", "c-sharp")]
    [InlineData("ts", "typescript")]
    [InlineData("java", "java")]
    [InlineData("cu", "cuda")]
    [InlineData("pgn", "pgn")]
    public void ResolvesFromNativeRegistry(string ext, string modality)
        => Assert.Equal(modality, GrammarDecomposer.ModalityByExt(ext));

    [Fact]
    public void UnknownExtReturnsNull()
        => Assert.Null(GrammarDecomposer.ModalityByExt("nope-not-an-ext"));
}
