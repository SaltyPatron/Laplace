namespace Laplace.Engine.Core;

/// <summary>
/// One named node of a Laplace AST — the engine's modality-agnostic parse output.
/// Mirrors C laplace_ast_node_t (sequential layout). No tree-sitter type is visible
/// here: the grammar-execution mechanism is sealed behind the engine, and this is the
/// Laplace-owned structure that composes into the tier tree like text words/sentences.
/// </summary>
public unsafe struct LaplaceAstNode
{
    /// <summary>Recipe-local node-type id; resolve to a name via GrammarAst.NodeTypeName.</summary>
    public uint NodeTypeId;

    /// <summary>[StartByte, EndByte) byte span of this node in the input.</summary>
    public uint StartByte;
    public uint EndByte;

    /// <summary>Index of the nearest named ancestor, or GrammarAst.Root for the root.</summary>
    public uint Parent;

    /// <summary>Node's subtree contains an ERROR/MISSING.</summary>
    public byte IsError;

    private fixed byte _pad[3];
}
