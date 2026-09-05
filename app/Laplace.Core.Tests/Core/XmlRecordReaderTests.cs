using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests;

public sealed class XmlRecordReaderTests
{
    [Fact]
    public async Task Records_PreserveNamespacesAttributesAndSplitText()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """
                <r xmlns="urn:root" xmlns:x="urn:x" a="top"><box><x:record id="α" x:id="β">ab&amp;<![CDATA[c]]><child>δ</child>e</x:record><other /></box></r>
                """);

            var frames = new List<XmlRecordFrame>();
            await foreach (XmlRecordFrame frame in XmlRecordReader.ReadAsync(
                path, recordDepth: 2, bufferSize: 1))
                frames.Add(frame);

            Assert.Collection(frames,
                root =>
                {
                    Assert.Equal(XmlRecordFrameKind.ContainerHeader, root.Kind);
                    Assert.Equal("r", root.Node.Name);
                    Assert.Equal("urn:root", root.Node.NamespaceUri);
                    Assert.Equal("top", root.Node.Attribute("a"));
                },
                box =>
                {
                    Assert.Equal(XmlRecordFrameKind.ContainerHeader, box.Kind);
                    Assert.Equal("box", box.Node.Name);
                    Assert.Equal("urn:root", box.Node.NamespaceUri);
                },
                record =>
                {
                    Assert.Equal(XmlRecordFrameKind.Record, record.Kind);
                    Assert.Equal("record", record.Node.Name);
                    Assert.Equal("urn:x", record.Node.NamespaceUri);
                    Assert.Equal("x", record.Node.Prefix);
                    Assert.Equal("ab&cδe", record.Node.Value);
                    Assert.Equal(2, record.Node.Attributes.Count);
                    Assert.Contains(record.Node.Attributes, attribute =>
                        attribute is { Name: "id", NamespaceUri: "", Prefix: "", Value: "α" });
                    Assert.Contains(record.Node.Attributes, attribute =>
                        attribute is { Name: "id", NamespaceUri: "urn:x", Prefix: "x", Value: "β" });
                    XmlRecordNode child = Assert.Single(record.Node.Children);
                    Assert.Equal("child", child.Name);
                    Assert.Equal("δ", child.Value);
                },
                other =>
                {
                    Assert.Equal(XmlRecordFrameKind.Record, other.Kind);
                    Assert.Equal("other", other.Node.Name);
                });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MalformedXml_FailsTheStream()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "<root><box><record></box></root>");
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await foreach (XmlRecordFrame _ in XmlRecordReader.ReadAsync(path, 2)) { }
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ManyRecords_DoNotAccumulateContainerTextOrChildren()
    {
        const int recordCount = 4_000;
        const string payload = "0123456789abcdef0123456789abcdef";
        string path = Path.GetTempFileName();
        try
        {
            await using (var writer = new StreamWriter(path))
            {
                await writer.WriteAsync("<root><records>");
                for (int i = 0; i < recordCount; ++i)
                    await writer.WriteAsync($"<record id=\"{i}\">{payload}</record>");
                await writer.WriteAsync("</records></root>");
            }

            int records = 0;
            int maxBufferedCharacters = 0;
            XmlRecordNode? rootHeader = null;
            XmlRecordNode? recordsHeader = null;
            await using var input = File.OpenRead(path);
            await foreach (XmlRecordFrame frame in XmlRecordReader.ReadAsync(
                input,
                recordDepth: 2,
                bufferSize: 127,
                observeBufferedCharacters: buffered =>
                    maxBufferedCharacters = Math.Max(maxBufferedCharacters, buffered)))
            {
                if (frame.Kind == XmlRecordFrameKind.Record) records++;
                else if (frame.Kind == XmlRecordFrameKind.ContainerHeader)
                {
                    if (frame.Node.Depth == 0) rootHeader = frame.Node;
                    else if (frame.Node.Depth == 1) recordsHeader = frame.Node;
                }
            }

            Assert.Equal(recordCount, records);
            Assert.NotNull(rootHeader);
            Assert.NotNull(recordsHeader);
            Assert.Empty(rootHeader.Value);
            Assert.Empty(rootHeader.Children);
            Assert.Empty(recordsHeader.Value);
            Assert.Empty(recordsHeader.Children);
            Assert.InRange(maxBufferedCharacters, payload.Length, payload.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
