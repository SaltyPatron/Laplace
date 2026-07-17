using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static unsafe class GrammarDecomposer
{
    public static IntPtr LookupById(string modalityId) =>
        NativeInterop.GrammarLookupById(modalityId);

    public static IntPtr LookupByExt(string ext) =>
        NativeInterop.GrammarLookupByExt(ext);

    public static string? ModalityByExt(string ext)
    {
        var p = NativeInterop.GrammarModalityByExt(ext);
        return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
    }

    public static GrammarAst Parse(ReadOnlySpan<byte> utf8, IntPtr recipe)
    {
        if (recipe == IntPtr.Zero)
            throw new ArgumentException("unknown modality recipe (zero handle)", nameof(recipe));
        IntPtr ast;
        fixed (byte* p = utf8)
        {
            int rc = NativeInterop.GrammarParse(p, (nuint)utf8.Length, recipe, &ast);
            if (rc != 0)
                throw new InvalidOperationException($"laplace_grammar_parse returned {rc}");
        }
        return new GrammarAst(ast);
    }

    public static GrammarAst Parse(ReadOnlySpan<byte> utf8, string modalityId)
    {
        var recipe = LookupById(modalityId);
        if (recipe == IntPtr.Zero)
            throw new ArgumentException($"unknown modality '{modalityId}'", nameof(modalityId));
        return Parse(utf8, recipe);
    }
}

public sealed unsafe class GrammarAst : IDisposable
{
    public const uint Root = uint.MaxValue;

    private IntPtr _ast;

    internal GrammarAst(IntPtr ast) => _ast = ast;

    public static GrammarAst Adopt(IntPtr ast) => new(ast);

    public IntPtr Handle => _ast;

    public int NodeCount
    {
        get
        {
            if (_ast == IntPtr.Zero) return 0;
            return checked((int)NativeInterop.AstNodeCount(_ast));
        }
    }

    public LaplaceAstNode GetNode(int index)
    {
        ObjectDisposedException.ThrowIf(_ast == IntPtr.Zero, this);
        LaplaceAstNode node;
        if (NativeInterop.AstGetNode(_ast, (nuint)index, &node) != 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        return node;
    }

    // ts_language_symbol_name returns pointers into the grammar's static
    // symbol-name table (process-lifetime), so the same (language, type_id)
    // always yields the same pointer - cache by pointer to marshal each
    // distinct name once per process instead of per AST node.
    private static readonly ConcurrentDictionary<IntPtr, string> TypeNameCache = new();

    public string? NodeTypeName(uint nodeTypeId)
    {
        ObjectDisposedException.ThrowIf(_ast == IntPtr.Zero, this);
        var p = NativeInterop.AstTypeName(_ast, nodeTypeId);
        if (p == IntPtr.Zero) return null;
        return TypeNameCache.TryGetValue(p, out var cached)
            ? cached
            : TypeNameCache.GetOrAdd(p, static q => Marshal.PtrToStringUTF8(q)!);
    }

    /// Zero-allocation type check: compares the native symbol name bytes
    /// against a UTF-8 literal (e.g. NodeTypeIs(id, "object"u8)).
    public bool NodeTypeIs(uint nodeTypeId, ReadOnlySpan<byte> utf8Name)
    {
        ObjectDisposedException.ThrowIf(_ast == IntPtr.Zero, this);
        var p = NativeInterop.AstTypeName(_ast, nodeTypeId);
        if (p == IntPtr.Zero) return false;
        byte* s = (byte*)p;
        for (int i = 0; i < utf8Name.Length; i++)
            if (s[i] != utf8Name[i]) return false;
        return s[utf8Name.Length] == 0;
    }

    public void Dispose()
    {
        if (_ast != IntPtr.Zero)
        {
            NativeInterop.AstFree(_ast);
            _ast = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~GrammarAst() => Dispose();
}
