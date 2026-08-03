using Laplace.Decomposers.Abstractions;
using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Every opening the ChessOpenings lane has deposited, keyed by the CONTENT ID of the
/// board it ends on. One hash lookup per ply.
///
/// WHY THIS EXISTS. The opening a game played was decided by
/// <see cref="OpeningClassifier"/>, which re-reads the ECO TSVs off disk and keys a
/// dictionary on <c>string.Join(' ', sans)</c> — a SAN-STRING PREFIX. That makes the
/// answer depend on MOVE ORDER, and chess openings transpose constantly. Measured over
/// 799 recorded lines on this box, comparing the classifier's verdict against the
/// deepest named position the game actually reached:
///
///     agree 490    disagree 309    (38.7% wrong or short)
///
/// with disagreements like these, position-id truth on the left:
///
///     Semi-Slav Defense: Meran, Stahlberg      vs  Queen's Pawn Game: Symmetrical
///     Semi-Slav Defense: Meran, Blumenfeld     vs  English Opening: Caro-Kann System
///     Bogo-Indian Defense: Retreat Variation   vs  Indian Defense: Anti-Nimzo-Indian
///     QGD: Orthodox Defense, Pillsbury         vs  Queen's Pawn Game: Zukertort
///     Sicilian: Najdorf, Main Line             vs  Sicilian: Najdorf
///
/// The first three are transpositions the string key cannot see; the rest are the key
/// stopping short of the deeper book line because its move order differed.
///
/// The substrate already had the right answer. Openings are ingested BEFORE games, and
/// the openings lane attests (final_position, OPENING_NAME, name) on a CONTENT-ADDRESSED
/// position — the identical id a game mints when it reaches that board, whatever order
/// it got there in. Measured: 1,625 of 3,733 named book positions are reached by games
/// in a 6,365-line sample. The join was sitting there; nothing read it.
///
/// So this is not an optimization of the classifier. Content addressing pays off by
/// COLLIDING, and a string prefix over a board game throws the collision away.
/// </summary>
internal sealed class ChessOpeningIndex : ChessOpeningIndexView
{
    private readonly Dictionary<Hash128, (Hash128 NameId, Hash128? EcoId)> _byPosition;

    private ChessOpeningIndex(Dictionary<Hash128, (Hash128, Hash128?)> byPosition)
        => _byPosition = byPosition;

    internal int Count => _byPosition.Count;

    /// <summary>O(1). The opening named on this exact board, or null.</summary>
    internal (Hash128 NameId, Hash128? EcoId)? Lookup(Hash128 positionId)
        => _byPosition.TryGetValue(positionId, out var hit) ? hit : null;

    /// <summary>
    /// The DEEPEST named position a replayed line passes through — the opening it
    /// actually reached, transpositions included.
    ///
    /// Deepest, not first: every prefix of an opening is itself a named position, so the
    /// first hit is always the shallowest label ("Queen's Pawn Game") and the last is the
    /// specific line ("QGD: Orthodox Defense, Pillsbury Variation"). Scanning forward and
    /// keeping the last hit is what lets a game that starts 1.c4 and transposes into the
    /// Semi-Slav be named the Semi-Slav.
    /// </summary>
    public (Hash128 NameId, Hash128? EcoId, int Ply)? DeepestMatch(IReadOnlyList<Hash128> positionIds)
    {
        (Hash128, Hash128?, int)? best = null;
        for (int i = 0; i < positionIds.Count; i++)
            if (Lookup(positionIds[i]) is { } hit)
                best = (hit.NameId, hit.EcoId, i);
        return best;
    }

    /// <summary>
    /// Load from the substrate. Empty (never null) when the openings lane has not run —
    /// the caller reports that rather than silently naming nothing, because an opening
    /// nobody attested is unattested, not attested-false.
    /// </summary>
    internal static async Task<ChessOpeningIndex> LoadAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        var map = new Dictionary<Hash128, (Hash128, Hash128?)>(4096);

        // One bounded read of the openings lane's own rows at Initialize — a few thousand
        // — then probed in memory. The read law's "aggregate ids, then batch" applied to a
        // lookup table: load once, never a query per record.
        //
        // The SQL lives in NpgsqlSubstrateReads, not here. ReadPathArchitectureGateTests
        // caught it inline in this file and was right: hand-written SQL in a consumer is
        // one more implementation for every caller to disagree with.
        var rows = await NpgsqlSubstrateReads.OpeningCatalogAsync(
            ds, RelOpeningName.ToBytes(), RelHasEco.ToBytes(),
            ChessVocabulary.OpeningsSourceId.ToBytes(), ct).ConfigureAwait(false);

        foreach (var (pos, name, eco) in rows)
        {
            // A position can carry more than one catalog name (the same board reached by two
            // catalogued lines). First wins, deterministically by scan order — the names are
            // synonyms for one board, so which one is a labelling choice, not a correctness
            // one.
            map.TryAdd(Hash128.FromBytes(pos),
                       (Hash128.FromBytes(name), eco is null ? null : Hash128.FromBytes(eco)));
        }
        return new ChessOpeningIndex(map);
    }

    private static readonly Hash128 RelOpeningName = RelationTypeRegistry.RelationTypeId(ChessSeedManifest.OpeningName);
    private static readonly Hash128 RelHasEco = RelationTypeRegistry.RelationTypeId(ChessSeedManifest.HasEco);
}
