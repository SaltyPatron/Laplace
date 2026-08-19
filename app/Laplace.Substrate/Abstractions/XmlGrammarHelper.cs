using System.Net;
using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Attribute-oriented access to an XML tree produced by the registered grammar.
/// This keeps source-specific witnesses on the common grammar admission path while
/// leaving interpretation of element and attribute names to the vendor decomposer.
/// </summary>
public static class XmlGrammarHelper
{
    public static IReadOnlyList<int> StartTags(
        GrammarAst ast, byte[] utf8, string elementName, int ancestorElement = -1)
    {
        byte[] expected = Encoding.UTF8.GetBytes(elementName);
        var matches = new List<int>();
        for (int i = 0; i < ast.NodeCount; i++)
        {
            var node = ast.GetNode(i);
            if (!ast.NodeTypeIs(node.NodeTypeId, "STag"u8)
                && !ast.NodeTypeIs(node.NodeTypeId, "EmptyElemTag"u8))
                continue;
            if (ancestorElement >= 0 && !IsDescendantOf(ast, i, ancestorElement))
                continue;
            if (TryDirectChild(ast, i, "Name"u8, out int nameNode)
                && SpanEquals(ast.GetNode(nameNode), utf8, expected))
                matches.Add(i);
        }
        return matches;
    }

    public static int ContainingElement(GrammarAst ast, int startTag)
    {
        uint parent = ast.GetNode(startTag).Parent;
        return parent != GrammarAst.Root
               && ast.NodeTypeIs(ast.GetNode((int)parent).NodeTypeId, "element"u8)
            ? (int)parent
            : -1;
    }

    public static bool TryAttribute(
        GrammarAst ast, byte[] utf8, int startTag, string attributeName, out string value)
    {
        byte[] expected = Encoding.UTF8.GetBytes(attributeName);
        for (int i = startTag + 1; i < ast.NodeCount; i++)
        {
            var node = ast.GetNode(i);
            if (node.Parent != (uint)startTag && !IsDescendantOf(ast, i, startTag)) break;
            if (node.Parent != (uint)startTag || !ast.NodeTypeIs(node.NodeTypeId, "Attribute"u8))
                continue;
            if (!TryDirectChild(ast, i, "Name"u8, out int nameNode)
                || !SpanEquals(ast.GetNode(nameNode), utf8, expected)
                || !TryDirectChild(ast, i, "AttValue"u8, out int valueNode))
                continue;

            var span = ast.GetNode(valueNode);
            int start = checked((int)span.StartByte);
            int length = checked((int)(span.EndByte - span.StartByte));
            if (length >= 2
                && (utf8[start] == (byte)'\"' || utf8[start] == (byte)'\'')
                && utf8[start + length - 1] == utf8[start])
            {
                start++;
                length -= 2;
            }
            value = WebUtility.HtmlDecode(Encoding.UTF8.GetString(utf8, start, length));
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryDirectChild(
        GrammarAst ast, int parent, ReadOnlySpan<byte> typeName, out int child)
    {
        for (int i = parent + 1; i < ast.NodeCount; i++)
        {
            var node = ast.GetNode(i);
            if (node.Parent != (uint)parent && !IsDescendantOf(ast, i, parent)) break;
            if (node.Parent == (uint)parent && ast.NodeTypeIs(node.NodeTypeId, typeName))
            {
                child = i;
                return true;
            }
        }
        child = -1;
        return false;
    }

    public static bool IsDescendantOf(GrammarAst ast, int nodeIndex, int ancestor)
    {
        uint parent = ast.GetNode(nodeIndex).Parent;
        while (parent != GrammarAst.Root)
        {
            if (parent == (uint)ancestor) return true;
            parent = ast.GetNode((int)parent).Parent;
        }
        return false;
    }

    private static bool SpanEquals(
        LaplaceAstNode node, byte[] utf8, ReadOnlySpan<byte> expected)
    {
        int start = checked((int)node.StartByte);
        int length = checked((int)(node.EndByte - node.StartByte));
        return length == expected.Length
               && utf8.AsSpan(start, length).SequenceEqual(expected);
    }
}
