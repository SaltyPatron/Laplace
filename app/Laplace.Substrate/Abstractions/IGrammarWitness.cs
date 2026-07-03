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
    int RootNodeIndex);
