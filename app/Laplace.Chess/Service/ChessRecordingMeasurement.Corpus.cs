namespace Laplace.Chess.Service;

internal sealed partial class ChessRecordingMeasurement
{
    private ChessCorpusPreparation? _corpusSource;
    private ChessCorpusEvidence? _corpusEvidence;
    private int _corpusConsumed;
    private int _streamedReadbackGames;
    private long _streamedReadbackPlies;
    private int _streamedScopes;
    private bool _corpusSourceUnchanged;
    private bool _corpusScopesUnchanged = true;

    internal bool IsCorpus => _corpusSource is not null;
    public ChessCorpusPreparation? CorpusSource => _corpusSource;
    public ChessCorpusEvidence.Receipt? CorpusEvidence => _corpusEvidence?.Summary;
    public string CompletionScope => IsCorpus
        ? "Complete original source PGN with finished header/movetext agreement and native legal full-line replay; human resignation/agreement allowed; no claim that every result is board-forced."
        : _normalMatchVerified ? "Native board-terminal completion bound to the completed CuteChess inventory."
        : "Recorded CuteChess match outcome; board-terminal completion is not established.";

    private bool CorpusSourceVerified => _corpusSourceUnchanged && _corpusConsumed == RequestedGames
        && _corpusEvidence?.Completed == true && _corpusEvidence.ReadbackGames == RequestedGames;

    private bool CorpusReplayScopesUnchanged => _streamedScopes > 0 && _corpusScopesUnchanged
        && ReplayScopes.All(scope => scope.Unchanged && ScopeRowsEqual(scope.Before, scope.After));

    internal static ChessRecordingMeasurement FromCorpus(
        ChessCorpusPreparation source, ChessCorpusEvidence evidence) => new(null, source.Selected.Count, retainedPgn: true)
    {
        _corpusSource = source,
        _corpusEvidence = evidence,
        Pgn = new(source.Source.Bytes, source.Source.Sha256),
    };

    private void ObserveCorpusParsed(ChessGameRecord game)
    {
        _corpusSource!.ValidateParsed(_corpusConsumed, game);
        _corpusConsumed++;
    }

    internal async Task FlushCorpusChunkAsync(int novel, CancellationToken ct)
    {
        if (_corpusEvidence is null)
            throw new InvalidOperationException("corpus recording has no evidence writer");
        // Exact verifier output is retained before dropping its per-chunk move graph.
        await _corpusEvidence.AppendAsync(Games, ReplayScopes, novel, Writer, ct);
        NovelGames += novel;
        AppliedGames += novel;
        _streamedReadbackGames += Games.Count;
        _streamedReadbackPlies += Games.Sum(game => (long)game.MoveIds.Length);
        _streamedScopes += ReplayScopes.Count;
        _corpusScopesUnchanged &= ReplayScopes.All(scope =>
            scope.Unchanged && ScopeRowsEqual(scope.Before, scope.After));
        Games.Clear();
        ReplayScopes.Clear();
    }
}
