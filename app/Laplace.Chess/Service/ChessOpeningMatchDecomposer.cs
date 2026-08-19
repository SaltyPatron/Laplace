using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

/// <summary>
/// Names each recorded line's opening by CONTENT-ADDRESSED BOARD IDENTITY: replay the
/// line, and the deepest position that collides with a position the ChessOpenings lane
/// named IS the opening. One hash probe per ply.
///
/// Run: <c>laplace ingest chess-opening-match</c>  (no path — the substrate is the source)
///
/// Matching law (catalog dual): deepest named board on the game path (id collision
/// with the openings catalog), or trajectory-prefix equality with an opening LINE.
/// SAN-prefix (<see cref="OpeningClassifier"/>) is retired as architecture — measured
/// 38.7% wrong/short (see <see cref="ChessOpeningIndex"/>); keep only as a weak
/// ChessAnalysis peer until consensus shows board/prefix wins.
///
/// Additive lane (not an in-place ChessAnalyze fix): attestation merge accumulates
/// observation_count, so re-deriving the calculated layer would double every witness.
/// Witnesses under separate sources:
///
///   ChessPgn          PGN Opening header (site claim)
///   ChessAnalysis     deprecated SAN-prefix peer against ECO TSV
///   ChessOpeningMatch board-identity / line-prefix against the ingested catalog
///
/// StandardsDerived: a board id collision is exact. Readers that want only this
/// verdict pass p_source to chess_opening_games.
///
/// PREREQUISITE, AND IT IS LOAD-BEARING: openings must be ingested before this runs.
/// They already are (openings is the cheap catalog lane, games are the expensive one), and
/// an empty index is reported as an unmet prerequisite rather than quietly naming nothing.
/// </summary>
public sealed class ChessOpeningMatchDecomposer
    : ComposeDecomposer<ChessOpeningMatchRecord>, IIngestNoOpExplainer
{
    /// <summary>
    /// Marker generation. Bump when the MATCHING RULE changes (which position wins),
    /// never when the catalog grows — a new opening simply names lines the previous run
    /// left unmatched, and those lines still carry no marker.
    /// </summary>
    public const int MatchVersion = 1;

    public static Hash128 MarkerId(Hash128 lineId)
        => Hash128.OfCanonical($"chess/opening-match/{lineId}/{MatchVersion}");

    public override Hash128 SourceId => ChessVocabulary.OpeningMatchSourceId;
    public override string SourceName => "ChessOpeningMatch";
    public override int LayerOrder => 21;
    public override Hash128 TrustClassId => ChessVocabulary.AnalysisTrustClass;
    protected override double SourceTrust => TC.StandardsDerived;
    protected override string BatchLabelPrefix => "chess/opening-match";

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;

    private const double MatchWeight = 0.9;

    internal const string ReplayFailed = "replay-failed";
    internal const string NoMoves = "no-moves-hydrated";
    internal const string NoCataloguedBoard = "no-catalogued-board";

    private ChessOpeningIndex? _index;
    private long _candidatesStreamed;
    private bool _catalogEmpty;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.OpeningMatchSourceId, SourceName,
            ChessVocabulary.AnalysisTrustClass, ct);

    protected override async IAsyncEnumerable<ChessOpeningMatchRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ContainmentReader is null
            || ChessWitnessHydrator.TryResolveDataSource(ContainmentReader) is not { } ds)
            throw new InvalidOperationException(
                "ChessOpeningMatch requires a live Postgres substrate (NpgsqlSubstrateReader). "
                + "Record games first: laplace ingest chess <pgn>");

        _candidatesStreamed = 0;
        _index = await ChessOpeningIndex.LoadAsync(ds, ct).ConfigureAwait(false);
        _catalogEmpty = _index.Count == 0;
        if (_catalogEmpty) yield break;   // ExplainEmptyRun names the unmet prerequisite

        Console.Out.WriteLine(
            $"CHESS_OPENING_INDEX positions={_index.Count} source=ChessOpenings");

        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options);
        ChessDropLedger.Reset();
        try
        {
            // LINE grain: the opening is a pure function of the play, so a line shared by a
            // thousand playings is matched ONCE — the same grain as trajectory and syzygy.
            await foreach (var witnessed in ChessWitnessHydrator.StreamUnanalyzedLinesAsync(
                               ds, ContainmentReader!, ws.Batch, MarkerId, ct))
            {
                _candidatesStreamed++;
                yield return new ChessOpeningMatchRecord(witnessed);
            }
        }
        finally { ChessDropLedger.Report(SourceName); }
    }

    protected override void Compose(ChessOpeningMatchRecord record, SubstrateChangeBuilder b)
        => Match(b, record.Game, _index!, SourceId);

    /// <summary>
    /// The pass as a pure function of a hydrated line and a catalog. Static and public so
    /// the matching rule is testable without a substrate.
    /// </summary>
    public static void Match(
        SubstrateChangeBuilder b, ChessWitnessedGame w, ChessOpeningIndexView index, Hash128 sourceId)
    {
        var positions = ReplayPositionIds(w);
        // A line that will not replay deposits NOTHING — not the match, not the marker —
        // so a later run retries it once the parser models the start (same refuse-not-invent
        // law as ChessAnalyze.InitialState and the trajectory lane). COUNTED, because the
        // first run of this lane deposited 53 markers for 6,365 streamed lines and the log
        // said status=ok: a silent skip is exactly the defect this campaign exists to remove.
        if (positions is null)
        {
            ChessDropLedger.Drop(ReplayFailed,
                $"line {w.LineId} plies={w.Moves.Count} startFen={w.StartFen ?? "<standard>"}");
            return;
        }
        if (w.Moves.Count == 0)
            ChessDropLedger.Drop(NoMoves, $"line {w.LineId}");

        // The marker is deposited even when no opening matched: "this line was checked
        // against catalog v1 and reached no named board" is a fact, and without it every
        // re-run re-replays every unmatched line forever.
        b.AddEntity(MarkerId(w.LineId), EntityTier.Document,
                    ChessVocabulary.AnalysisMarkerType, sourceId);

        if (index.DeepestMatch(positions) is not { } hit)
        {
            // Checked and reached no catalogued board. A real answer, not a failure —
            // the marker above records that the check happened.
            ChessDropLedger.Drop(NoCataloguedBoard, $"line {w.LineId} plies={positions.Count - 1}");
            return;
        }
        ChessDropLedger.Kept();

        b.AddAttestation(NativeAttestation.Categorical(
            w.LineId, ChessSeedManifest.GameHasOpening, hit.NameId, sourceId, null, MatchWeight));
        if (hit.EcoId is { } ecoId)
            b.AddAttestation(NativeAttestation.Categorical(
                w.LineId, ChessSeedManifest.GameHasEco, ecoId, sourceId, null, MatchWeight));
    }

    /// <summary>
    /// The ordered content ids of the boards this line passes through, start included, or
    /// null when a SAN will not resolve. Ids only via <see cref="ChessCompose.PositionId"/> —
    /// not <see cref="ChessCompose.Position"/> / PositionMemo (heap stand-in; #822 is ROM).
    /// </summary>
    internal static List<Hash128>? ReplayPositionIds(ChessWitnessedGame w)
    {
        var m = new ChessModality();
        if (ChessAnalyze.InitialState(w.StartFen, m) is not { } start) return null;
        var state = start.Initial;
        var ids = new List<Hash128>(w.Moves.Count + 1);
        lock (ChessCompose.Gate)
        {
            ids.Add(ChessCompose.PositionId(m.StateKey(state)));
            foreach (var san in w.Moves)
            {
                var mv = San.Resolve(state.Board, m.LegalActions(state), san);
                if (mv is null) return null;
                state = m.Apply(state, mv.Value);
                ids.Add(ChessCompose.PositionId(m.StateKey(state)));
            }
        }
        return ids;
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return Task.FromResult<long?>(null);
        return ChessWitnessHydrator.CountRecordedLinesAsync(ds, ct);
    }

    /// <summary>
    /// An empty run is expected when the backfill has caught up. An empty CATALOG is a
    /// different thing and says so: the prerequisite lane has not run, and naming zero
    /// openings because the catalog is missing must never read as "these games have no
    /// opening".
    /// </summary>
    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
    {
        if (_catalogEmpty)
            return ("dependency-unset",
                "ChessOpeningMatch: the ChessOpenings catalog is empty — no OPENING_NAME "
                + "attestations to match against. Ingest it first: laplace ingest openings "
                + "<dir>. Unattested is not attested-false; nothing was named.");
        if (_candidatesStreamed == 0)
            return ("already-complete",
                $"ChessOpeningMatch: every one of {declaredInputUnits} recorded line(s) already "
                + $"carries the v{MatchVersion} match marker — nothing left to name.");
        return null;
    }
}

/// <summary>The catalog surface Match() needs — an interface so the rule is testable with a fake.</summary>
public interface ChessOpeningIndexView
{
    (Hash128 NameId, Hash128? EcoId, int Ply)? DeepestMatch(IReadOnlyList<Hash128> positionIds);
}

/// <summary>Trunk root is this lane's marker, so batches never collide with another lane's.</summary>
public sealed record ChessOpeningMatchRecord(ChessWitnessedGame Game) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessOpeningMatchDecomposer.MarkerId(Game.LineId);
}
