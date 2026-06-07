using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// The shared, modality-agnostic decomposition mechanism. A grammar is the recipe for
/// a structured-knowledge modality; this resolves a recipe and parses bytes into a
/// Laplace AST. Domain decomposers (Code, Chess, ...) sit on top, adding relation kinds
/// and trust. The grammar-execution mechanism (compiled into laplace_core) never leaks
/// past this surface.
/// </summary>
public static unsafe class GrammarDecomposer
{
    /// <summary>Recipe handle for a modality id ("python","json",...). Zero if unknown.</summary>
    public static IntPtr LookupById(string modalityId) => NativeInterop.GrammarLookupById(modalityId);

    /// <summary>Recipe handle by file extension ("py","json",...). Zero if unknown.</summary>
    public static IntPtr LookupByExt(string ext) => NativeInterop.GrammarLookupByExt(ext);

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

/// <summary>
/// Owns a native Laplace AST. Pre-order, named nodes only: a node's parent always
/// precedes it, so reverse-index iteration composes bottom-up (children before parents).
/// </summary>
public sealed unsafe class GrammarAst : IDisposable
{
    public const uint Root = uint.MaxValue; // LAPLACE_AST_ROOT

    private IntPtr _ast;

    internal GrammarAst(IntPtr ast) => _ast = ast;

    public int NodeCount =>
        _ast == IntPtr.Zero ? 0 : checked((int)NativeInterop.AstNodeCount(_ast));

    public LaplaceAstNode GetNode(int index)
    {
        ObjectDisposedException.ThrowIf(_ast == IntPtr.Zero, this);
        LaplaceAstNode node;
        if (NativeInterop.AstGetNode(_ast, (nuint)index, &node) != 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        return node;
    }

    public string? KindName(uint kindId)
    {
        ObjectDisposedException.ThrowIf(_ast == IntPtr.Zero, this);
        var p = NativeInterop.AstKindName(_ast, kindId);
        return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
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
