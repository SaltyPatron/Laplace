using System.Collections.Concurrent;
using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

public static class JsonGrammarHelper
{
    private static readonly ConcurrentDictionary<string, byte[]> PropertyUtf8Cache = new(StringComparer.Ordinal);

    public static bool TryComposedProperty(
        in GrammarComposeContext ctx, string property, out Hash128 entityId)
        => TryComposedPropertyOnObject(ctx, RootObjectNode(ctx), property, out entityId);

    /// Resolves the root object node on demand: for a JSON row the first
    /// object node sits at the top of the AST, so this terminates in a couple
    /// of probes; contexts constructed with an explicit index keep it.
    public static int RootObjectNode(in GrammarComposeContext ctx) =>
        ctx.RootNodeIndex == GrammarComposeContext.UnresolvedRootNode
            ? FindRootObjectNode(ctx.Ast)
            : ctx.RootNodeIndex;

    public static bool TryComposedPropertyOnObject(
        in GrammarComposeContext ctx, int objectNodeIndex, string property, out Hash128 entityId)
    {
        entityId = default;
        if (ctx.Composer is null) return false;
        if (!TryObjectPropertyStringSpan(ctx.Ast, ctx.Utf8, objectNodeIndex, property, out uint start, out uint end))
            return false;
        if (TryContentRootFromJsonStringSpan(ctx.Utf8, start, end, out entityId))
            return true;
        return ctx.Composer.TrySpanEntity(start, end, out entityId);
    }

    public static bool TryPropertyUtf8(
        in GrammarComposeContext ctx, string property, out ReadOnlySpan<byte> utf8)
        => TryPropertyUtf8OnObject(ctx, RootObjectNode(ctx), property, out utf8);

    public static bool TryPropertyUtf8OnObject(
        in GrammarComposeContext ctx, int objectNodeIndex, string property, out ReadOnlySpan<byte> utf8)
    {
        utf8 = default;
        if (!TryObjectPropertyStringSpan(ctx.Ast, ctx.Utf8, objectNodeIndex, property, out uint start, out uint end))
            return false;
        return TryDecodedSpan(ctx.Utf8, start, end, out utf8);
    }

    public static bool IsEmptyOrDashPlaceholder(in GrammarComposeContext ctx, int objectNodeIndex, string property)
    {
        if (!TryPropertyUtf8OnObject(ctx, objectNodeIndex, property, out var utf8))
            return true;
        if (utf8.IsEmpty) return true;
        return utf8.Length == 1 && utf8[0] == (byte)'-';
    }

    public static IEnumerable<int> ObjectNodesInArrayProperty(GrammarComposeContext ctx, string arrayProperty)
        => ObjectNodesInArrayOnObject(ctx, RootObjectNode(ctx), arrayProperty);

    public static IEnumerable<int> StringNodesInArrayOnObject(
        GrammarComposeContext ctx, int objectNodeIndex, string arrayProperty)
    {
        if (!TryObjectArrayPropertyNode(ctx.Ast, ctx.Utf8, objectNodeIndex, arrayProperty, out int arrayNode))
            yield break;
        foreach (int child in ChildrenOf(ctx.Ast, arrayNode))
        {
            if (ctx.Ast.NodeTypeIs(ctx.Ast.GetNode(child).NodeTypeId, "string"u8))
                yield return child;
        }
    }

    public static IEnumerable<int> ObjectNodesInArrayOnObject(
        GrammarComposeContext ctx, int objectNodeIndex, string arrayProperty)
    {
        if (!TryObjectArrayPropertyNode(ctx.Ast, ctx.Utf8, objectNodeIndex, arrayProperty, out int arrayNode))
            yield break;
        foreach (int child in ChildrenOf(ctx.Ast, arrayNode))
        {
            if (ctx.Ast.NodeTypeIs(ctx.Ast.GetNode(child).NodeTypeId, "object"u8))
                yield return child;
        }
    }

    public static int FindRootObjectNode(GrammarAst ast)
    {
        for (int i = 0; i < ast.NodeCount; i++)
        {
            if (ast.NodeTypeIs(ast.GetNode(i).NodeTypeId, "object"u8))
                return i;
        }
        return -1;
    }

    public static IEnumerable<(int KeyStringNode, int ValueNode)> EnumerateObjectPairs(
        GrammarAst ast, int objectNodeIndex)
    {
        if (objectNodeIndex < 0) yield break;
        foreach (int i in ChildrenOf(ast, objectNodeIndex))
        {
            if (!ast.NodeTypeIs(ast.GetNode(i).NodeTypeId, "pair"u8)) continue;
            int key = PairKeyChild(ast, i);
            int value = PairValueChild(ast, i);
            if (key < 0 || value < 0) continue;
            yield return (key, value);
        }
    }

    public static bool TryKeyUtf8(GrammarAst ast, byte[] utf8, int keyStringNode, out ReadOnlySpan<byte> keyUtf8)
    {
        keyUtf8 = default;
        return TryStringNodeSpan(ast, keyStringNode, out uint start, out uint end)
            && TryDecodedSpan(utf8, start, end, out keyUtf8);
    }

    public static bool IsObjectNode(GrammarAst ast, int nodeIndex) =>
        nodeIndex >= 0 && ast.NodeTypeIs(ast.GetNode(nodeIndex).NodeTypeId, "object"u8);

    public static bool IsArrayNode(GrammarAst ast, int nodeIndex) =>
        nodeIndex >= 0 && ast.NodeTypeIs(ast.GetNode(nodeIndex).NodeTypeId, "array"u8);

    public static IEnumerable<int> StringNodesInArray(GrammarAst ast, int arrayNodeIndex)
    {
        if (!IsArrayNode(ast, arrayNodeIndex)) yield break;
        foreach (int i in ChildrenOf(ast, arrayNodeIndex))
        {
            if (ast.NodeTypeIs(ast.GetNode(i).NodeTypeId, "string"u8))
                yield return i;
        }
    }

    public static bool TryComposedNode(
        GrammarComposeContext ctx, int nodeIndex, out Hash128 entityId)
    {
        entityId = default;
        if (ctx.Composer is null) return false;
        if (TryStringNodeSpan(ctx.Ast, nodeIndex, out uint start, out uint end))
            return TryContentRootFromJsonStringSpan(ctx.Utf8, start, end, out entityId)
                || ctx.Composer.TrySpanEntity(start, end, out entityId);
        var nd = ctx.Ast.GetNode(nodeIndex);
        return ctx.Composer.TrySpanEntity(nd.StartByte, nd.EndByte, out entityId);
    }

    public static bool Utf8EqualsProperty(ReadOnlySpan<byte> quotedOrRaw, string property)
    {
        ReadOnlySpan<byte> inner = Unquote(quotedOrRaw);
        return inner.SequenceEqual(PropertyUtf8(property));
    }

    public static string Utf8ToString(ReadOnlySpan<byte> quotedOrRaw) =>
        Encoding.UTF8.GetString(DecodeJsonStringUtf8(Unquote(quotedOrRaw)));

    public static IEnumerable<int> ChildNodesInObjectArray(
        GrammarComposeContext ctx, int objectNodeIndex, string arrayProperty)
    {
        if (!TryObjectArrayPropertyNode(ctx.Ast, ctx.Utf8, objectNodeIndex, arrayProperty, out int arrayNode))
            yield break;
        foreach (int i in ChildrenOf(ctx.Ast, arrayNode))
            yield return i;
    }

    public static int FindNestedObject(in GrammarComposeContext ctx, int objectNodeIndex, string property)
    {
        foreach (int i in ChildrenOf(ctx.Ast, objectNodeIndex))
        {
            if (!ctx.Ast.NodeTypeIs(ctx.Ast.GetNode(i).NodeTypeId, "pair"u8)) continue;
            if (!PairKeyMatches(ctx.Ast, ctx.Utf8, i, property)) continue;
            int value = PairValueChild(ctx.Ast, i);
            if (value < 0) return -1;
            if (ctx.Ast.NodeTypeIs(ctx.Ast.GetNode(value).NodeTypeId, "object"u8))
                return value;
        }
        return -1;
    }

    private static ReadOnlySpan<byte> PropertyUtf8(string property) =>
        PropertyUtf8Cache.GetOrAdd(property, static p => Encoding.UTF8.GetBytes(p));

    internal static bool TryContentRootFromJsonStringSpan(
        byte[] utf8, uint start, uint end, out Hash128 entityId)
    {
        entityId = default;
        if (!TryDecodedCanonicalBytes(utf8, start, end, out byte[] decoded))
            return false;
        if (ContentTierSpine.ResolveRoot(decoded) is not Hash128 root)
            return false;
        entityId = root;
        return true;
    }

    private static bool TryDecodedSpan(byte[] utf8, uint start, uint end, out ReadOnlySpan<byte> decoded)
    {
        decoded = default;
        if (!Slice(utf8, start, end, out var raw)) return false;
        ReadOnlySpan<byte> inner = Unquote(raw);
        if (!ContainsJsonEscapes(inner))
        {
            decoded = inner;
            return true;
        }
        decoded = DecodeJsonStringUtf8(inner);
        return true;
    }

    private static bool TryDecodedCanonicalBytes(byte[] utf8, uint start, uint end, out byte[] decoded)
    {
        decoded = Array.Empty<byte>();
        if (!Slice(utf8, start, end, out var raw)) return false;
        ReadOnlySpan<byte> inner = Unquote(raw);
        decoded = ContainsJsonEscapes(inner) ? DecodeJsonStringUtf8(inner) : inner.ToArray();
        return decoded.Length > 0;
    }

    private static bool ContainsJsonEscapes(ReadOnlySpan<byte> inner)
    {
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == (byte)'\\') return true;
        }
        return false;
    }

    internal static byte[] DecodeJsonStringUtf8(ReadOnlySpan<byte> inner)
    {
        if (inner.IsEmpty) return Array.Empty<byte>();
        var sb = new byte[inner.Length];
        int w = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            byte c = inner[i];
            if (c != (byte)'\\' || i + 1 >= inner.Length)
            {
                sb[w++] = c;
                continue;
            }
            byte esc = inner[++i];
            switch (esc)
            {
                case (byte)'"': sb[w++] = (byte)'"'; break;
                case (byte)'\\': sb[w++] = (byte)'\\'; break;
                case (byte)'/': sb[w++] = (byte)'/'; break;
                case (byte)'b': sb[w++] = (byte)'\b'; break;
                case (byte)'f': sb[w++] = (byte)'\f'; break;
                case (byte)'n': sb[w++] = (byte)'\n'; break;
                case (byte)'r': sb[w++] = (byte)'\r'; break;
                case (byte)'t': sb[w++] = (byte)'\t'; break;
                case (byte)'u' when i + 4 < inner.Length:
                    if (TryParseHex4(inner.Slice(i + 1, 4), out int codepoint))
                    {
                        i += 4;
                        w += Encoding.UTF8.GetBytes(char.ConvertFromUtf32(codepoint), 0, 1, sb, w);
                    }
                    break;
                default:
                    sb[w++] = esc;
                    break;
            }
        }
        return sb.AsSpan(0, w).ToArray();
    }

    private static bool TryParseHex4(ReadOnlySpan<byte> hex, out int value)
    {
        value = 0;
        if (hex.Length < 4) return false;
        for (int i = 0; i < 4; i++)
        {
            int d = HexDigit(hex[i]);
            if (d < 0) return false;
            value = (value << 4) | d;
        }
        return true;
    }

    private static int HexDigit(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => c - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - (byte)'A' + 10,
        _ => -1,
    };

    private static IEnumerable<int> ChildrenOf(GrammarAst ast, int parentIndex)
    {
        uint parent = (uint)parentIndex;
        for (int i = 0; i < ast.NodeCount; i++)
        {
            if (ast.GetNode(i).Parent == parent)
                yield return i;
        }
    }

    private static bool TryObjectPropertyStringSpan(
        GrammarAst ast, byte[] utf8, int objectNodeIndex, string property, out uint start, out uint end)
    {
        start = end = 0;
        if (objectNodeIndex < 0) return false;
        foreach (int i in ChildrenOf(ast, objectNodeIndex))
        {
            if (!ast.NodeTypeIs(ast.GetNode(i).NodeTypeId, "pair"u8)) continue;
            if (!PairKeyMatches(ast, utf8, i, property)) continue;
            return TryPairValueStringSpan(ast, i, out start, out end);
        }
        return false;
    }

    private static bool TryObjectArrayPropertyNode(
        GrammarAst ast, byte[] utf8, int objectNodeIndex, string property, out int arrayNode)
    {
        arrayNode = -1;
        foreach (int i in ChildrenOf(ast, objectNodeIndex))
        {
            if (!ast.NodeTypeIs(ast.GetNode(i).NodeTypeId, "pair"u8)) continue;
            if (!PairKeyMatches(ast, utf8, i, property)) continue;
            int value = PairValueChild(ast, i);
            if (value < 0) return false;
            if (!ast.NodeTypeIs(ast.GetNode(value).NodeTypeId, "array"u8)) return false;
            arrayNode = value;
            return true;
        }
        return false;
    }

    private static bool PairKeyMatches(GrammarAst ast, byte[] utf8, int pairIndex, string property)
    {
        int key = PairKeyChild(ast, pairIndex);
        if (key < 0) return false;
        var nd = ast.GetNode(key);
        return Slice(utf8, nd.StartByte, nd.EndByte, out var span) && Utf8EqualsProperty(span, property);
    }

    private static bool TryPairValueStringSpan(GrammarAst ast, int pairIndex, out uint start, out uint end)
    {
        start = end = 0;
        int value = PairValueChild(ast, pairIndex);
        if (value < 0) return false;
        return TryStringNodeSpan(ast, value, out start, out end);
    }

    private static bool TryStringNodeSpan(GrammarAst ast, int nodeIndex, out uint start, out uint end)
    {
        start = end = 0;
        var nd = ast.GetNode(nodeIndex);
        string? t = ast.NodeTypeName(nd.NodeTypeId);
        if (t == "string")
        {
            start = nd.StartByte;
            end = nd.EndByte;
            return end > start;
        }
        if (t == "string_content")
        {
            start = nd.StartByte;
            end = nd.EndByte;
            return true;
        }
        return false;
    }

    private static int PairKeyChild(GrammarAst ast, int pairIndex)
    {
        foreach (int i in ChildrenOf(ast, pairIndex))
        {
            if (ast.NodeTypeIs(ast.GetNode(i).NodeTypeId, "string"u8))
                return i;
        }
        return -1;
    }

    private static int PairValueChild(GrammarAst ast, int pairIndex)
    {
        int key = PairKeyChild(ast, pairIndex);
        foreach (int i in ChildrenOf(ast, pairIndex))
        {
            if (i == key) continue;
            string? t = ast.NodeTypeName(ast.GetNode(i).NodeTypeId);
            if (t is "object" or "array" or "string" or "number" or "true" or "false" or "null")
                return i;
        }
        return -1;
    }

    private static ReadOnlySpan<byte> Unquote(ReadOnlySpan<byte> span) =>
        span.Length >= 2 && span[0] == (byte)'"' && span[^1] == (byte)'"' ? span[1..^1] : span;

    private static bool Slice(byte[] utf8, uint start, uint end, out ReadOnlySpan<byte> span)
    {
        span = default;
        if (end < start || end > utf8.Length) return false;
        span = utf8.AsSpan((int)start, (int)(end - start));
        return true;
    }
}
