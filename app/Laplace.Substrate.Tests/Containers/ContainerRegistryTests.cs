using System.Runtime.CompilerServices;
using Xunit;
using Laplace.Decomposers.Containers.Abstractions;

namespace Laplace.Decomposers.Containers.Abstractions.Tests;

public class ContainerRegistryTests
{
    private sealed class FakeParser : IContainerParser
    {
        public string FormatName { get; }
        private readonly byte[] _magic;
        public FakeParser(string name, byte[] magic) { FormatName = name; _magic = magic; }
        public bool CanParse(ReadOnlySpan<byte> magic) =>
            magic.Length >= _magic.Length && magic.Slice(0, _magic.Length).SequenceEqual(_magic);
        public async IAsyncEnumerable<ExplodedViewItem> DissectAsync(
            Stream content, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new Entry(FormatName, 0, 0);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public void Resolve_ReturnsNullWhenNoParserMatches()
    {
        var reg = new ContainerRegistry();
        reg.Register(new FakeParser("a", new byte[] { 0x01 }));
        Assert.Null(reg.Resolve(new byte[] { 0x99 }));
    }

    [Fact]
    public void Resolve_ReturnsMatchingParser()
    {
        var reg = new ContainerRegistry();
        var pA = new FakeParser("a", new byte[] { 0x01, 0x02 });
        var pB = new FakeParser("b", new byte[] { 0x03 });
        reg.Register(pA);
        reg.Register(pB);
        Assert.Same(pA, reg.Resolve(new byte[] { 0x01, 0x02, 0xFF }));
        Assert.Same(pB, reg.Resolve(new byte[] { 0x03, 0xFF }));
    }

    [Fact]
    public void Resolve_LastRegisteredWinsForAmbiguousMagic()
    {
        var reg = new ContainerRegistry();
        var first = new FakeParser("first", new byte[] { 0xAA });
        var second = new FakeParser("second", new byte[] { 0xAA });
        reg.Register(first);
        reg.Register(second);
        Assert.Same(second, reg.Resolve(new byte[] { 0xAA }));
    }

    [Fact]
    public void Parsers_ReturnsSnapshotInRegistrationOrder()
    {
        var reg = new ContainerRegistry();
        var a = new FakeParser("a", new byte[] { 1 });
        var b = new FakeParser("b", new byte[] { 2 });
        reg.Register(a);
        reg.Register(b);
        var got = reg.Parsers.ToList();
        Assert.Equal(2, got.Count);
        Assert.Same(a, got[0]);
        Assert.Same(b, got[1]);
    }

    [Fact]
    public void Register_RejectsNull()
    {
        var reg = new ContainerRegistry();
        Assert.Throws<ArgumentNullException>(() => reg.Register(null!));
    }

    [Fact]
    public void ExplodedViewItem_RecordEqualityByValue()
    {
        var a = new Entry("foo", 0, 100);
        var b = new Entry("foo", 0, 100);
        Assert.Equal(a, b);
        var c = a with { Length = 200 };
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void TensorReference_PreservesShapeAndDtype()
    {
        var t = new TensorReference("layers.0.weight", "BF16", new[] { 4096L, 11008L },
                                     ByteOffset: 1024, ByteLength: 4096L * 11008 * 2);
        Assert.Equal("BF16", t.Dtype);
        Assert.Equal(2, t.Shape.Count);
        Assert.Equal(4096L, t.Shape[0]);
    }

    [Fact]
    public void PythonClassReference_DocumentsStaticOnlyResolution()
    {
        var r = new PythonClassReference("model.pkl", "torch.nn", "Linear");
        Assert.Equal("torch.nn", r.Module);
        Assert.Equal("Linear", r.Qualname);
    }
}
