using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public class DelimitedContentTests
{
    [Fact]
    public void Split_SemicolonGloss_YieldsTrimmedUnits()
    {
        var units = DelimitedContent.Split(
            "a temple in Jerusalem; the first built by Solomon; destroyed by Babylon", ';');
        Assert.Equal(
            new[] { "a temple in Jerusalem", "the first built by Solomon", "destroyed by Babylon" },
            units);
    }

    [Fact]
    public void Split_DropsEmptyAndWhitespaceUnits()
    {
        var units = DelimitedContent.Split("one ;  ; two ;", ';');
        Assert.Equal(new[] { "one", "two" }, units);
    }

    [Fact]
    public void Split_NoDelimiterInContent_ReturnsSingleTrimmedUnit()
    {
        var units = DelimitedContent.Split("  a single definition  ", ';');
        Assert.Equal(new[] { "a single definition" }, units);
    }

    [Fact]
    public void Split_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(DelimitedContent.Split("", ';'));
        Assert.Empty(DelimitedContent.Split("   ", ';'));
    }

    [Fact]
    public void Split_NoDelimitersGiven_ReturnsWholeTrimmed()
    {
        var units = DelimitedContent.Split("  whole content  ");
        Assert.Equal(new[] { "whole content" }, units);
    }
}
