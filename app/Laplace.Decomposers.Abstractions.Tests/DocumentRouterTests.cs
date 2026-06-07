using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class DocumentRouterTests
{
    private static string Text(string doc, DocumentSpan s) => doc.Substring(s.Start, s.Length);

    [Fact]
    public void PlainProse_IsOneProseSpan()
    {
        var doc = "just some prose\nwith two lines\n";
        var spans = DocumentRouter.Split(doc);
        Assert.Single(spans);
        Assert.Equal(SpanKind.Prose, spans[0].Kind);
        Assert.Equal(doc, Text(doc, spans[0]));
    }

    [Fact]
    public void FencedPython_Splits_Prose_Code_Prose()
    {
        var doc = "To read a file:\n```python\nopen(path).read()\n```\nThat's it.\n";
        var spans = DocumentRouter.Split(doc);
        Assert.Equal(3, spans.Count);

        Assert.Equal(SpanKind.Prose, spans[0].Kind);
        Assert.Equal("To read a file:\n", Text(doc, spans[0]));

        Assert.Equal(SpanKind.Code, spans[1].Kind);
        Assert.Equal("python", spans[1].Language);
        Assert.Equal("open(path).read()\n", Text(doc, spans[1]));

        Assert.Equal(SpanKind.Prose, spans[2].Kind);
        Assert.Equal("That's it.\n", Text(doc, spans[2]));
    }

    [Fact]
    public void FenceWithoutLanguage_HasNullLanguage()
    {
        var doc = "```\nraw text\n```\n";
        var spans = DocumentRouter.Split(doc);
        var code = Assert.Single(spans, s => s.Kind == SpanKind.Code);
        Assert.Null(code.Language);
        Assert.Equal("raw text\n", Text(doc, code));
    }

    [Fact]
    public void Empty_YieldsNoSpans()
    {
        Assert.Empty(DocumentRouter.Split(""));
    }

    [Fact]
    public void UnclosedFence_CodeRunsToEnd()
    {
        var doc = "intro\n```rust\nfn main() {}\n";
        var spans = DocumentRouter.Split(doc);
        Assert.Equal(2, spans.Count);
        Assert.Equal(SpanKind.Prose, spans[0].Kind);
        Assert.Equal(SpanKind.Code, spans[1].Kind);
        Assert.Equal("rust", spans[1].Language);
        Assert.Equal("fn main() {}\n", Text(doc, spans[1]));
    }

    [Fact]
    public void MultipleBlocks_Ordered_NonOverlapping_InBounds()
    {
        var doc = "a\n```js\nb\n```\nc\n```py\nd\n```\ne\n";
        var spans = DocumentRouter.Split(doc);

        int prevEnd = 0;
        foreach (var s in spans)
        {
            Assert.True(s.Start >= prevEnd, "spans must be ordered and non-overlapping");
            Assert.True(s.Start + s.Length <= doc.Length, "spans must stay in bounds");
            Assert.True(s.Length > 0, "no empty spans");
            prevEnd = s.Start + s.Length;
        }

        var codes = spans.Where(s => s.Kind == SpanKind.Code).ToList();
        Assert.Equal(2, codes.Count);
        Assert.Equal("js", codes[0].Language);
        Assert.Equal("py", codes[1].Language);
    }
}
