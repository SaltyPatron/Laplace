using System.Globalization;
using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// JSON navigation over the REGISTERED grammar route (tree-sitter `json` via
/// <see cref="GrammarDecomposer.Parse(ReadOnlySpan{byte}, string)"/>), so consumers
/// never hand-roll a parser for a format the registry already unpacks (the
/// DecomposerProjects_NoHandRolledParserForARegisteredGrammar law). One document =
/// one parsed AST plus a per-parent child index; cursors are cheap value handles.
/// </summary>
public sealed class JsonAstDocument : IDisposable
{
    private readonly GrammarAst _ast;
    internal readonly byte[] Utf8;
    private readonly int[] _childStart;
    private readonly int[] _childList;
    private readonly int _rootIndex;

    private JsonAstDocument(GrammarAst ast, byte[] utf8)
    {
        _ast = ast;
        Utf8 = utf8;
        int n = ast.NodeCount;

        // One pass builds the parent→children adjacency in node order (tree-sitter
        // emits preorder, so per-parent buckets keep source order).
        var counts = new int[n + 1];
        for (int i = 0; i < n; i++)
        {
            uint parent = ast.GetNode(i).Parent;
            if (parent != GrammarAst.Root && parent < (uint)n) counts[(int)parent + 1]++;
        }
        for (int i = 1; i <= n; i++) counts[i] += counts[i - 1];
        _childStart = counts;
        _childList = new int[n == 0 ? 0 : _childStart[n]];
        var fill = new int[n];
        for (int i = 0; i < n; i++)
        {
            uint parent = ast.GetNode(i).Parent;
            if (parent == GrammarAst.Root || parent >= (uint)n) continue;
            int p = (int)parent;
            _childList[_childStart[p] + fill[p]++] = i;
        }

        _rootIndex = FindRootValue(ast, n);
    }

    private static int FindRootValue(GrammarAst ast, int n)
    {
        for (int i = 0; i < n; i++)
        {
            uint typeId = ast.GetNode(i).NodeTypeId;
            if (ast.NodeTypeIs(typeId, "object"u8) || ast.NodeTypeIs(typeId, "array"u8))
                return i;
        }
        return -1;
    }

    public static JsonAstDocument? TryParse(byte[] utf8)
    {
        if (utf8.Length == 0) return null;
        GrammarAst ast;
        try { ast = GrammarDecomposer.Parse(utf8, "json"); }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
        var doc = new JsonAstDocument(ast, utf8);
        if (doc._rootIndex < 0) { doc.Dispose(); return null; }
        return doc;
    }

    public static JsonAstDocument? TryParse(string text) =>
        TryParse(Encoding.UTF8.GetBytes(text));

    public JsonAstCursor Root => new(this, _rootIndex);

    internal GrammarAst Ast => _ast;

    internal ReadOnlySpan<int> ChildrenOf(int node) =>
        node < 0 || node + 1 >= _childStart.Length
            ? []
            : _childList.AsSpan(_childStart[node], _childStart[node + 1] - _childStart[node]);

    public void Dispose() => _ast.Dispose();
}

public enum JsonAstKind { None, Object, Array, String, Number, True, False, Null }

/// <summary>A value node inside a <see cref="JsonAstDocument"/>.</summary>
public readonly struct JsonAstCursor(JsonAstDocument doc, int node)
{
    private readonly JsonAstDocument _doc = doc;
    private readonly int _node = node;

    public bool IsValid => _doc is not null && _node >= 0;

    public JsonAstKind Kind
    {
        get
        {
            if (!IsValid) return JsonAstKind.None;
            uint typeId = _doc.Ast.GetNode(_node).NodeTypeId;
            if (_doc.Ast.NodeTypeIs(typeId, "object"u8)) return JsonAstKind.Object;
            if (_doc.Ast.NodeTypeIs(typeId, "array"u8)) return JsonAstKind.Array;
            if (_doc.Ast.NodeTypeIs(typeId, "string"u8)) return JsonAstKind.String;
            if (_doc.Ast.NodeTypeIs(typeId, "number"u8)) return JsonAstKind.Number;
            if (_doc.Ast.NodeTypeIs(typeId, "true"u8)) return JsonAstKind.True;
            if (_doc.Ast.NodeTypeIs(typeId, "false"u8)) return JsonAstKind.False;
            if (_doc.Ast.NodeTypeIs(typeId, "null"u8)) return JsonAstKind.Null;
            return JsonAstKind.None;
        }
    }

    public bool IsObject => Kind == JsonAstKind.Object;
    public bool IsArray => Kind == JsonAstKind.Array;

    /// <summary>The property's VALUE node, whatever its type; invalid cursor when absent.</summary>
    public JsonAstCursor Property(string name)
    {
        if (!IsValid || !IsObject) return default;
        foreach (int pair in _doc.ChildrenOf(_node))
        {
            if (!_doc.Ast.NodeTypeIs(_doc.Ast.GetNode(pair).NodeTypeId, "pair"u8)) continue;
            (int key, int value) = PairChildren(pair);
            if (key < 0 || value < 0) continue;
            var kn = _doc.Ast.GetNode(key);
            if (Slice(kn.StartByte, kn.EndByte) is { } span
                && JsonGrammarHelper.Utf8EqualsProperty(span, name))
                return new JsonAstCursor(_doc, value);
        }
        return default;
    }

    public string? String(string name) => Property(name).AsString();
    public long? Int64(string name) => Property(name).AsInt64();

    public bool? Bool(string name) => Property(name).Kind switch
    {
        JsonAstKind.True => true,
        JsonAstKind.False => false,
        _ => null,
    };

    /// <summary>Decoded text of a string node (JSON escapes resolved); null otherwise.</summary>
    public string? AsString()
    {
        if (!IsValid || Kind != JsonAstKind.String) return null;
        var nd = _doc.Ast.GetNode(_node);
        return Slice(nd.StartByte, nd.EndByte) is { } span
            ? JsonGrammarHelper.Utf8ToString(span)
            : null;
    }

    public long? AsInt64()
    {
        if (!IsValid || Kind != JsonAstKind.Number) return null;
        var nd = _doc.Ast.GetNode(_node);
        return Slice(nd.StartByte, nd.EndByte) is { } span
               && long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)
            ? v
            : null;
    }

    public double? AsDouble()
    {
        if (!IsValid || Kind != JsonAstKind.Number) return null;
        var nd = _doc.Ast.GetNode(_node);
        return Slice(nd.StartByte, nd.EndByte) is { } span
               && double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : null;
    }

    /// <summary>The node's raw JSON text — the GetRawText of this route.</summary>
    public string? RawText()
    {
        if (!IsValid) return null;
        var nd = _doc.Ast.GetNode(_node);
        return Slice(nd.StartByte, nd.EndByte) is { } span ? Encoding.UTF8.GetString(span) : null;
    }

    /// <summary>Array items (value nodes), in source order.</summary>
    public IEnumerable<JsonAstCursor> Items()
    {
        if (!IsValid || !IsArray) yield break;
        var doc = _doc;
        foreach (int child in doc.ChildrenOf(_node).ToArray())
        {
            var cursor = new JsonAstCursor(doc, child);
            if (cursor.Kind != JsonAstKind.None) yield return cursor;
        }
    }

    /// <summary>Object pairs as (decoded key, value cursor), in source order.</summary>
    public IEnumerable<(string Key, JsonAstCursor Value)> Pairs()
    {
        if (!IsValid || !IsObject) yield break;
        var doc = _doc;
        foreach (int pair in doc.ChildrenOf(_node).ToArray())
        {
            if (!doc.Ast.NodeTypeIs(doc.Ast.GetNode(pair).NodeTypeId, "pair"u8)) continue;
            (int key, int value) = PairChildren(pair);
            if (key < 0 || value < 0) continue;
            var kn = doc.Ast.GetNode(key);
            if (SliceOf(doc, kn.StartByte, kn.EndByte) is not { } span) continue;
            yield return (JsonGrammarHelper.Utf8ToString(span), new JsonAstCursor(doc, value));
        }
    }

    private (int Key, int Value) PairChildren(int pairNode)
    {
        int key = -1, value = -1;
        foreach (int child in _doc.ChildrenOf(pairNode))
        {
            var cursor = new JsonAstCursor(_doc, child);
            if (key < 0 && cursor.Kind == JsonAstKind.String) { key = child; continue; }
            if (cursor.Kind != JsonAstKind.None) { value = child; break; }
        }
        return (key, value);
    }

    private byte[]? Slice(uint start, uint end) => SliceOf(_doc, start, end);

    private static byte[]? SliceOf(JsonAstDocument doc, uint start, uint end) =>
        end >= start && end <= doc.Utf8.Length
            ? doc.Utf8.AsSpan((int)start, (int)(end - start)).ToArray()
            : null;
}
