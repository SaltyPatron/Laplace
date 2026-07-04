using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

// The CALCULATED pass. A second decomposer over the SAME PGN, gated by the analysis-version
// watermark: for each game NOT yet analyzed at the current version, it replays the movetext and
// derives positions / geometry / move edges / motifs / opening / consensus (ChessAnalyze) under
// the ChessAnalysis source, and stamps a per-(game, version) marker so the next run skips it.
//
// Ingest = record (ChessPgnDecomposer, witnessed, no engine). This = analyze (derive, deferred,
// re-runnable, targetable). Run: `laplace ingest chess-analyze <pgn path>`. See .scratchpad/08.
public sealed class ChessAnalyzeDecomposer : IDecomposer
{
    public Hash128 SourceId => ChessVocabulary.AnalysisSourceId;
    public string SourceName => "ChessAnalysis";
    public int LayerOrder => 21; // after ChessPgn (20)
    public Hash128 TrustClassId => ChessVocabulary.AnalysisTrustClass;

    private IReadOnlyCollection<string> _canonicalNames = System.Array.Empty<string>();
    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.AnalysisSourceId, SourceName, ChessVocabulary.AnalysisTrustClass, ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int batch = System.Math.Clamp(options.BatchSize > 1 ? options.BatchSize : 256, 1, 512);
        await foreach (var change in DecomposerBatch.RunAsync(
            StreamUnanalyzedGamesAsync(context.EcosystemPath, context.Reader, batch, ct),
            (parsed, b) => ChessAnalyze.DeriveFromParsed(b, parsed),
            ChessVocabulary.AnalysisSourceId, "chess/analysis", batch, context.Reader, options, ct))
            yield return change;
    }

    // Stream games not yet analyzed at the current version. Same chunked bulk-probe pattern the
    // recorder uses for novelty, but keyed on the analysis marker rather than the game id.
    private static async IAsyncEnumerable<ChessPgnDecomposer.ParsedGame> StreamUnanalyzedGamesAsync(
        string ecosystemPath, ISubstrateReader? reader, int chunkSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var chunk = new List<ChessPgnDecomposer.ParsedGame>(chunkSize);
        await foreach (var gameText in ChessPgnDecomposer.StreamAllGamesAsync(ecosystemPath, ct))
        {
            if (ChessPgnDecomposer.TryParseGame(gameText) is { } parsed) chunk.Add(parsed);
            if (chunk.Count < chunkSize) continue;
            await foreach (var g in FilterUnanalyzedAsync(chunk, reader, ct)) yield return g;
            chunk.Clear();
        }
        await foreach (var g in FilterUnanalyzedAsync(chunk, reader, ct)) yield return g;
    }

    internal static async IAsyncEnumerable<ChessPgnDecomposer.ParsedGame> FilterUnanalyzedAsync(
        List<ChessPgnDecomposer.ParsedGame> chunk, ISubstrateReader? reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (chunk.Count == 0) yield break;
        if (reader is null) { foreach (var g in chunk) yield return g; yield break; }

        var markers = new Hash128[chunk.Count];
        for (int i = 0; i < chunk.Count; i++)
            markers[i] = ChessVocabulary.AnalysisMarkerId(chunk[i].GameId, ChessAnalyze.Version);

        byte[] bm = await reader.EntitiesExistBitmapAsync(markers, ct).ConfigureAwait(false);
        long bits = (long)bm.Length * 8;
        for (int i = 0; i < chunk.Count; i++)
        {
            bool present = i < bits && (bm[i >> 3] & (1 << (i & 7))) != 0;
            if (!present) yield return chunk[i];
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
