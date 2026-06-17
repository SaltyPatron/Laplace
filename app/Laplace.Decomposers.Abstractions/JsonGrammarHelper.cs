using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;





public static class JsonGrammarHelper
{
    public static bool TryComposedProperty(
        in GrammarComposeContext ctx, string property, out Hash128 entityId)
    {
        entityId = default;
        if (ctx.Composer is null) return false;
        if (!TryPropertyStringSpan(ctx.Ast, ctx.Utf8, property, out uint start, out uint end))
            return false;
        return ctx.Composer.TrySpanEntity(start, end, out entityId);
    }

    public static bool TryComposedPropertyOnObject(
        in GrammarComposeContext ctx, int objectNodeIndex, string property, out Hash128 entityId)
    {
        entityId = default;
        if (ctx.Composer is null) return false;
        if (!TryObjectPropertyStringSpan(ctx.Ast, ctx.Utf8, objectNodeIndex, property, out uint start, out uint end))
            return false;
        return ctx.Composer.TrySpanEntity(start, end, out entityId);
    }

    public static bool TryPropertyUtf8(
        in GrammarComposeContext ctx, string property, out ReadOnlySpan<byte> utf8)
    {
        utf8 = default;
        return TryPropertyStringSpan(ctx.Ast, ctx.Utf8, property, out uint start, out uint end)
            && Slice(ctx.Utf8, start, end, out utf8);
    }

    public static bool TryPropertyUtf8OnObject(
        in GrammarComposeContext ctx, int objectNodeIndex, string property, out ReadOnlySpan<byte> utf8)
    {
        utf8 = default;
        return TryObjectPropertyStringSpan(ctx.Ast, ctx.Utf8, objectNodeIndex, property, out uint start, out uint end)
            && Slice(ctx.Utf8, start, end, out utf8);
    }

    public static IEnumerable<int> ObjectNodesInArrayProperty(GrammarComposeContext ctx, string arrayProperty)
    {
        if (!TryArrayPropertyNode(ctx.Ast, ctx.Utf8, arrayProperty, out int arrayNode))
            yield break;
        for (int i = 0; i < ctx.Ast.NodeCount; i++)
        {
            var nd = ctx.Ast.GetNode(i);
            if (nd.Parent == (uint)arrayNode && ctx.Ast.NodeTypeName(nd.NodeTypeId) == "object")
                yield return i;
        }
    }

    public static IEnumerable<int> StringNodesInArrayOnObject(
        GrammarComposeContext ctx, int objectNodeIndex, string arrayProperty)
    {
        if (!TryObjectArrayPropertyNode(ctx.Ast, ctx.Utf8, objectNodeIndex, arrayProperty, out int arrayNode))
            yield break;
        for (int i = 0; i < ctx.Ast.NodeCount; i++)
        {
            var nd = ctx.Ast.GetNode(i);
            if (nd.Parent == (uint)arrayNode && ctx.Ast.NodeTypeName(nd.NodeTypeId) == "string")
                yield return i;
        }
    }

    public static IEnumerable<int> ObjectNodesInArrayOnObject(
        GrammarComposeContext ctx, int objectNodeIndex, string arrayProperty)
    {
        if (!TryObjectArrayPropertyNode(ctx.Ast, ctx.Utf8, objectNodeIndex, arrayProperty, out int arrayNode))
            yield break;
        for (int i = 0; i < ctx.Ast.NodeCount; i++)
        {
            var nd = ctx.Ast.GetNode(i);
            if (nd.Parent == (uint)arrayNode && ctx.Ast.NodeTypeName(nd.NodeTypeId) == "object")
                yield return i;
        }
    }

    public static bool TryComposedNode(
        GrammarComposeContext ctx, int nodeIndex, out Hash128 entityId)
    {
        entityId = default;
        if (ctx.Composer is null) return false;
        if (TryStringNodeSpan(ctx.Ast, nodeIndex, out uint start, out uint end))
            return ctx.Composer.TrySpanEntity(start, end, out entityId);
        var nd = ctx.Ast.GetNode(nodeIndex);
        return ctx.Composer.TrySpanEntity(nd.StartByte, nd.EndByte, out entityId);
    }

    public static bool Utf8EqualsProperty(ReadOnlySpan<byte> quotedOrRaw, string property)
    {
        ReadOnlySpan<byte> inner = Unquote(quotedOrRaw);
        return inner.SequenceEqual(Encoding.UTF8.GetBytes(property));
    }

    public static string Utf8ToString(ReadOnlySpan<byte> quotedOrRaw) =>
        Encoding.UTF8.GetString(Unquote(quotedOrRaw));

    public static IEnumerable<int> ChildNodesInObjectArray(
        GrammarComposeContext ctx, int objectNodeIndex, string arrayProperty)
    {
        if (!TryObjectArrayPropertyNode(ctx.Ast, ctx.Utf8, objectNodeIndex, arrayProperty, out int arrayNode))
            yield break;
        for (int i = 0; i < ctx.Ast.NodeCount; i++)
        {
            if (ctx.Ast.GetNode(i).Parent == (uint)arrayNode)
                yield return i;
        }
    }

    public static int FindNestedObject(in GrammarComposeContext ctx, int objectNodeIndex, string property)
    {
        for (int i = 0; i < ctx.Ast.NodeCount; i++)
        {
            var nd = ctx.Ast.GetNode(i);
            if (nd.Parent != (uint)objectNodeIndex) continue;
            if (ctx.Ast.NodeTypeName(nd.NodeTypeId) != "pair") continue;
            if (!PairKeyMatches(ctx.Ast, ctx.Utf8, i, property)) continue;
            int value = PairValueChild(ctx.Ast, i);
            if (value < 0) return -1;
            if (ctx.Ast.NodeTypeName(ctx.Ast.GetNode(value).NodeTypeId) == "object")
                return value;
        }
        return -1;
    }

    private static bool TryPropertyStringSpan(
        GrammarAst ast, byte[] utf8, string property, out uint start, out uint end)
    {
        start = end = 0;
        for (int i = 0; i < ast.NodeCount; i++)
        {
            if (ast.NodeTypeName(ast.GetNode(i).NodeTypeId) != "pair") continue;
            if (!PairKeyMatches(ast, utf8, i, property)) continue;
            return TryPairValueStringSpan(ast, i, out start, out end);
        }
        return false;
    }

    private static bool TryObjectPropertyStringSpan(
        GrammarAst ast, byte[] utf8, int objectNodeIndex, string property, out uint start, out uint end)
    {
        start = end = 0;
        if (objectNodeIndex < 0) return false;
        for (int i = 0; i < ast.NodeCount; i++)
        {
            var nd = ast.GetNode(i);
            if (nd.Parent != (uint)objectNodeIndex) continue;
            if (ast.NodeTypeName(nd.NodeTypeId) != "pair") continue;
            if (!PairKeyMatches(ast, utf8, i, property)) continue;
            return TryPairValueStringSpan(ast, i, out start, out end);
        }
        return false;
    }

    private static bool TryArrayPropertyNode(GrammarAst ast, byte[] utf8, string property, out int arrayNode)
    {
        arrayNode = -1;
        for (int i = 0; i < ast.NodeCount; i++)
        {
            if (ast.NodeTypeName(ast.GetNode(i).NodeTypeId) != "pair") continue;
            if (!PairKeyMatches(ast, utf8, i, property)) continue;
            int value = PairValueChild(ast, i);
            if (value < 0) return false;
            if (ast.NodeTypeName(ast.GetNode(value).NodeTypeId) != "array") return false;
            arrayNode = value;
            return true;
        }
        return false;
    }

    private static bool TryObjectArrayPropertyNode(
        GrammarAst ast, byte[] utf8, int objectNodeIndex, string property, out int arrayNode)
    {
        arrayNode = -1;
        for (int i = 0; i < ast.NodeCount; i++)
        {
            var nd = ast.GetNode(i);
            if (nd.Parent != (uint)objectNodeIndex) continue;
            if (ast.NodeTypeName(nd.NodeTypeId) != "pair") continue;
            if (!PairKeyMatches(ast, utf8, i, property)) continue;
            int value = PairValueChild(ast, i);
            if (value < 0) return false;
            if (ast.NodeTypeName(ast.GetNode(value).NodeTypeId) != "array") return false;
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
        if (t == "string_content")
        {
            start = nd.StartByte;
            end = nd.EndByte;
            return true;
        }
        if (t != "string") return false;
        for (int i = 0; i < ast.NodeCount; i++)
        {
            var ch = ast.GetNode(i);
            if (ch.Parent != (uint)nodeIndex) continue;
            if (ast.NodeTypeName(ch.NodeTypeId) == "string_content")
            {
                start = ch.StartByte;
                end = ch.EndByte;
                return true;
            }
        }
        start = nd.StartByte;
        end = nd.EndByte;
        return true;
    }

    private static int PairKeyChild(GrammarAst ast, int pairIndex)
    {
        for (int i = 0; i < ast.NodeCount; i++)
        {
            if (ast.GetNode(i).Parent != (uint)pairIndex) continue;
            if (ast.NodeTypeName(ast.GetNode(i).NodeTypeId) == "string")
                return i;
        }
        return -1;
    }

    private static int PairValueChild(GrammarAst ast, int pairIndex)
    {
        bool seenKey = false;
        for (int i = 0; i < ast.NodeCount; i++)
        {
            if (ast.GetNode(i).Parent != (uint)pairIndex) continue;
            if (ast.NodeTypeName(ast.GetNode(i).NodeTypeId) != "string") continue;
            if (!seenKey) { seenKey = true; continue; }
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
