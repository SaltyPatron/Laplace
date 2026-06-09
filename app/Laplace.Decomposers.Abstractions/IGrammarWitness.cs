using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Thin witness contract: map composed grammar field spans to semantic attestations.
/// Parsing, composition, and PRECEDES live in laplace_core (Tier 0).
/// </summary>
public interface IGrammarWitness
{
    string ModalityId { get; }

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
    GrammarRowComposer? Composer);
