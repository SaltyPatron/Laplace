using System.Runtime.CompilerServices;
using System.Xml;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Shared XML file enumeration for PropBank framesets and VerbNet VNCLASS roots.
/// Pure extract — load document, yield matching root element per file.
/// </summary>
public static class SharedXmlFramesetReader
{
    public static async IAsyncEnumerable<XmlElement> ReadRootsAsync(
        IEnumerable<string> files,
        string expectedRootName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var doc = new XmlDocument();
            try { doc.Load(file); }
            catch (XmlException) { continue; }
            var root = doc.DocumentElement;
            if (root is null || !root.Name.Equals(expectedRootName, StringComparison.Ordinal))
                continue;
            yield return root;
        }
    }

    public static IEnumerable<XmlElement> DescendantElements(XmlElement el, string name)
    {
        foreach (XmlNode node in el.GetElementsByTagName(name))
            if (node is XmlElement ce) yield return ce;
    }

    public static IEnumerable<XmlElement> ChildElements(XmlElement parent, string containerTag, string childTag)
    {
        foreach (var container in DescendantElements(parent, containerTag))
            foreach (XmlNode node in container.ChildNodes)
                if (node is XmlElement ce && ce.Name.Equals(childTag, StringComparison.Ordinal))
                    yield return ce;
    }

    public static IEnumerable<XmlElement> DirectChildren(XmlElement parent, string tag)
    {
        foreach (XmlNode node in parent.ChildNodes)
            if (node is XmlElement ce && ce.Name.Equals(tag, StringComparison.Ordinal))
                yield return ce;
    }

    public static IEnumerable<string> EnumerateXmlFiles(string primaryDir, string? fallbackDir = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in new[] { primaryDir, fallbackDir })
        {
            if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, "*.xml")
                                       .OrderBy(p => p, StringComparer.Ordinal))
                if (seen.Add(Path.GetFullPath(f)))
                    yield return f;
        }
    }

    public static IEnumerable<string> EnumerateFramesetFiles(string framesDir, string ecosystemPath)
    {
        string? parent = string.Equals(
            Path.GetFullPath(framesDir).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(ecosystemPath).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase)
            ? null
            : Path.GetDirectoryName(framesDir);
        return EnumerateXmlFiles(framesDir, parent);
    }
}
