using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>
/// The chess source's abstract symbols — the ONLY things minted by name (OfCanonical + Vocabulary
/// tier): the source, the <c>Chess_Position</c> type, and the <c>MOVE</c> relation type. Position
/// instances are never declared here — they are composed from their canonical surface (routed through
/// <c>ContentEmitter</c>), so their id/tier/coordinate emerge. (When the substrate-wide anchor class is
/// removed, this vocab moves to composed content too — see the vocabulary-is-content direction.)
/// </summary>
public static class ChessVocabulary
{
    public const string SourceName = "ChessSelfPlay";

    public static readonly Hash128 SourceId     = Hash128.OfCanonical("substrate/source/ChessSelfPlay/v1");
    public static readonly Hash128 PositionType = Hash128.OfCanonical("substrate/type/Chess_Position/v1");
    public static readonly Hash128 MoveType     = Hash128.OfCanonical("substrate/type/MOVE/v1");

    /// <summary>Trust of the self-play corpus (the substrate's own play); tune as needed.</summary>
    public const double Trust = SourceTrust.StructuredCorpus;

    public static async Task BootstrapAsync(ISubstrateWriter writer, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(SourceId, SourceName, trustClassId: SourceId);
        boot.AddType("Chess_Position");
        boot.AddRelationType("MOVE");
        await writer.ApplyAsync(boot.Build(), ct);
    }
}
