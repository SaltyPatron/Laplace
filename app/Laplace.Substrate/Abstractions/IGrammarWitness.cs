using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;





public interface IGrammarWitness
{
    string ModalityId { get; }

    bool TrunkShortcircuitWithoutCompose => false;

    void WalkRow(
        in GrammarComposeContext composed,
        in RowContext ctx,
        SubstrateChangeBuilder builder);
}

public readonly record struct RowContext(
    int RowIndex,
    long InputUnitsConsumed,
    Hash128? ContextId = null,
    IReadOnlyDictionary<string, int>? ColumnIndex = null);

public readonly record struct GrammarComposeContext(
    byte[] Utf8,
    GrammarAst Ast,
    Hash128 RootId,
    GrammarRowComposer? Composer,
    int RootNodeIndex = GrammarComposeContext.UnresolvedRootNode)
{
    // Sentinel: root object node not yet resolved. JsonGrammarHelper resolves
    // on demand, so non-JSON rows (TSV lanes) never pay the AST scan that an
    // eager FindRootObjectNode costs — it can never match there.
    public const int UnresolvedRootNode = -2;
}
