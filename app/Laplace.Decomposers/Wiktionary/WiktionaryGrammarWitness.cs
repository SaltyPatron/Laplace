using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Wiktionary;

/// <summary>
/// Grammar-witness adapter for wiktextract JSON rows. Historically this witness ran on
/// the <c>GrammarIngestDecomposer</c> tree-sitter spine (one AST + O(n²) node walk per
/// row). The bulk path is now a native <see cref="System.Text.Json.Utf8JsonReader"/>
/// parse (<see cref="WiktionaryDecomposer"/> compose lane); this adapter remains an
/// <see cref="IGrammarWitness"/> so a grammar row can still be witnessed on demand —
/// it re-parses the row bytes with the SAME native parser and emits through the SAME
/// <see cref="WiktionaryEmit"/>, so the AST is never touched.
/// </summary>
internal sealed class WiktionaryGrammarWitness : IGrammarWitness
{
    private readonly DecomposerOptions _options;

    public WiktionaryGrammarWitness(DecomposerOptions options) => _options = options;

    public string ModalityId => "json";

    public void WalkRow(
        in GrammarComposeContext composed,
        in RowContext ctx,
        SubstrateChangeBuilder builder)
    {
        // Ignore the tree-sitter AST entirely: parse the raw row bytes natively.
        var entry = WiktionaryEntry.Parse(composed.Utf8, _options);
        if (entry is not null)
            WiktionaryEmit.Emit(entry, builder);
    }
}
