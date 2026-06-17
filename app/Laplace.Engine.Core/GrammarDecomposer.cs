using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;








public static unsafe class GrammarDecomposer
{
    
    public static IntPtr LookupById(string modalityId) => NativeInterop.GrammarLookupById(modalityId);

    
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





public sealed unsafe class GrammarAst : IDisposable
{
    public const uint Root = uint.MaxValue; 

    private IntPtr _ast;

    internal GrammarAst(IntPtr ast) => _ast = ast;

    public static GrammarAst Adopt(IntPtr ast) => new(ast);

    public IntPtr Handle => _ast;

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

    public string? NodeTypeName(uint nodeTypeId)
    {
        ObjectDisposedException.ThrowIf(_ast == IntPtr.Zero, this);
        var p = NativeInterop.AstTypeName(_ast, nodeTypeId);
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
