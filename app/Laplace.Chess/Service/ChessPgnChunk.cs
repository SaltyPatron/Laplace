using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>
/// One complete-game composition window for the ordinary PGN writer. The byte estimate
/// controls producer accumulation; native capture/apply still enforces its actual grant.
/// Grouping changes operational intent/source-unit context IDs, while canonical game and
/// object IDs and raw source-body multiplicity remain unchanged.
/// </summary>
internal sealed class ChessPgnChunk : IDisposable
{
    internal List<ChessGameRecord> Games { get; } = [];
    internal SubstrateChangeBuilder Record { get; } =
        new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "chess/lab/ingest")
            .DeclareSourcePrior(SourceTrust.StructuredCorpus);
    internal SubstrateChangeBuilder Analyze { get; } =
        new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "chess/lab/ingest")
            .DeclareSourcePrior(SourceTrust.StructuredCorpus)
            .DeclareSourcePrior(ChessTransitions.SourceId, SourceTrust.StructuredCorpus)
            .DeclareSourcePrior(ChessPositionOutcomes.SourceId, SourceTrust.StructuredCorpus);
    internal SubstrateChangeBuilder Repair { get; } =
        new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "chess/lab/repair-playing")
            .DeclareSourcePrior(SourceTrust.StructuredCorpus);
    internal HashSet<Hash128> RepairPlayings { get; } = [];
    internal HashSet<Hash128> ObservedPositions { get; } = [];
    internal HashSet<Hash128> ObservedMoves { get; } = [];
    internal int NovelGames { get; private set; }
    internal long StagedBytesBeforeLastGame { get; private set; }
    internal long ModeledSourceAdmissionBytesBeforeLastGame { get; private set; }
    internal long ModeledSourceAdmissionBytes =>
        SubstrateChangeBuilder.ModeledSourceAdmissionPayloadBytes(Record, Analyze, Repair);
    internal long StagedBytes => checked(
        Record.StagedBytesEstimate + Analyze.StagedBytesEstimate + Repair.StagedBytesEstimate);

    internal static ChessPgnChunk ComposeNext(
        IReadOnlyList<ChessGameRecord> games, IReadOnlySet<Hash128> novelIds, ref int offset,
        long stagedByteBudget, int? exactGameCount = null,
        ChessRecordingMeasurement? measurement = null, CancellationToken ct = default)
    {
        if (stagedByteBudget < 1) throw new ArgumentOutOfRangeException(nameof(stagedByteBudget));
        if (offset < 0 || offset >= games.Count) throw new ArgumentOutOfRangeException(nameof(offset));
        if (exactGameCount is { } exact && (exact < 1 || exact > games.Count - offset))
            throw new InvalidDataException("corpus replay source does not cover its next sealed chunk");

        var chunk = new ChessPgnChunk();
        if (measurement is not null) measurement.Work.ChunksStarted++;
        measurement?.Checkpoint("CompositionAndNoveltyProbe", "chunk-started", chunkGames: 0);
        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var game = games[offset];
                chunk.StagedBytesBeforeLastGame = chunk.StagedBytes;
                chunk.ModeledSourceAdmissionBytesBeforeLastGame = chunk.ModeledSourceAdmissionBytes;
                chunk.Add(game, novelIds.Contains(game.PlayingId), measurement);
                offset++;
                // The source game is atomic. Never truncate moves or observations to fit
                // either estimate; close after the game crosses the existing shared
                // byte share. The source-local model includes known capture/plan
                // coexistence but cannot predict provider expansion. An oversized
                // single game still reaches the actual runtime grant intact.
                // Exact replay follows the fresh evidence schedule, whose per-game contents
                // and no-write scope are independently checked by ChessCorpusEvidence.
                if (exactGameCount is { } count
                    ? chunk.Games.Count == count
                    : chunk.StagedBytes >= stagedByteBudget
                        || chunk.ModeledSourceAdmissionBytes >= stagedByteBudget)
                    break;
            }
            while (offset < games.Count);
            return chunk;
        }
        catch
        {
            chunk.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Record.Dispose();
        Analyze.Dispose();
        Repair.Dispose();
    }

    // The original probe spans a nominal parse window. Only a successful ordinary
    // apply/readback proves that a later repeated PLAYING in that window is no longer
    // novel. Failed or unacknowledged chunks must leave this state untouched.
    internal void ForgetCommittedNovelty(ISet<Hash128> remainingNovelIds)
    {
        foreach (var game in Games) remainingNovelIds.Remove(game.PlayingId);
    }

    private void Add(ChessGameRecord game, bool novel, ChessRecordingMeasurement? measurement)
    {
        if (novel)
        {
            NovelGames++;
            measurement?.Checkpoint("CompositionAndNoveltyProbe", "game-composition-entered", periodic: true,
                chunkGames: Games.Count);
            ChessPgnDecomposer.RecordGame(game, Record);
            // One parsed replay feeds the same calculated owners as the unsplit path.
            var replay = ChessPgnDecomposer.MaterializeParsedReplay(game);
            ChessAnalyze.DeriveFromParsed(Analyze, game, replay);
            ChessTransitions.DepositFromParsed(Analyze, game);
            ChessPositionOutcomes.DepositFromParsed(Analyze, game, replay);
            var prober = ChessTablebaseRuntime.Prober;
            if (measurement is not null) measurement.Work.SyzygyAvailable = prober is not null;
            if (prober is not null)
            {
                using var syzygyPhase = measurement?.MeasurePhase(ChessRecordingMeasurement.WorkPhase.Syzygy);
                if (measurement is not null) measurement.Work.SyzygyGameCalls++;
                ChessSyzygy.DeriveGame(Analyze, ChessAnalyze.WitnessedFromParsed(game), prober);
                if (measurement is not null) measurement.Work.SyzygyGameCallsCompleted++;
            }
            if (measurement is not null)
            {
                measurement.Work.NovelGamesComposed++;
                measurement.Work.NovelPliesComposed += game.MoveIds.Length;
                measurement.Work.PositionOccurrencesComposed += game.PositionIds.Length;
            }
            for (int i = 0; i + 1 < game.PositionIds.Length; i++)
                ObservedPositions.Add(game.PositionIds[i]);
            foreach (var moveId in game.MoveIds)
                ObservedMoves.Add(moveId);
        }
        else
        {
            RepairPlayings.Add(game.PlayingId);
            // Reuse the current recorded projection, then let the unchanged writer
            // owner retain only playing-scoped evidence absent from durable storage.
            ChessPgnDecomposer.RecordGame(game, Repair);
            if (measurement is not null) measurement.Work.RepairGamesComposed++;
        }
        Games.Add(game);
        measurement?.Checkpoint("CompositionAndNoveltyProbe",
            novel ? "game-completed" : "repair-game-completed", periodic: true, chunkGames: Games.Count);
    }
}
