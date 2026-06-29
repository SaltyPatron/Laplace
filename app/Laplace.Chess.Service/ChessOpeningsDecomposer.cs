using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>
/// Ingests the Lichess ECO opening book — the <c>openings/*.tsv</c> files, one named line per row:
/// <c>eco⇥name⇥pgn</c> (e.g. <c>A00 ⇥ Amar Opening ⇥ 1. Nh3</c>). The <c>pgn</c> movetext column is
/// parsed by the SAME <c>pgn</c> tree-sitter grammar the game-PGN path uses (<see cref="PgnMovetext"/>),
/// then each line is replayed through the perft-verified movegen (<see cref="San.Resolve"/>) and emitted
/// via the shared <see cref="ChessGraph"/> — so opening positions and moves fold into the IDENTICAL graph
/// self-play and game ingest use (converge, not fork). Streams row-by-row, O(one line) RAM.
///
/// <para><b>Scoring — openings are theory, not games.</b> An opening line carries NO win/loss, so each
/// ply is recorded as a <see cref="PlyOutcome.Draw"/>: after the fold its eff_mu sits at ~neutral
/// (1500×1e9), which marks the move as RECOGNISED book — ranked above the unrated-novelty prior
/// (800×1e9) — WITHOUT falsely claiming the line wins (the book lists dubious gambits too; the real
/// who-wins signal is the millions of rated games). The observation count
/// (<c>LAPLACE_OPENING_GAMES</c>, default 4) gives book lines a small, bounded trust: enough to be
/// rated, far too little to override real game evidence (witness shrink K0 = 15000). Deterministic
/// "always play book" belongs in an explicit opening-book lookup, not in inflated ratings.</para>
/// </summary>
public sealed class ChessOpeningsDecomposer : IDecomposer
{
    public Hash128 SourceId     => ChessVocabulary.OpeningsSourceId;
    public string  SourceName   => "ChessOpenings";
    public int     LayerOrder   => 20;
    public Hash128 TrustClassId => ChessVocabulary.OpeningsTrustClass;

    private const int    LinesPerBatch       = 256;
    private const double OpeningWitnessWeight = 0.7;   // match the game-PGN path → constant φ per relation

    /// <summary>Observation count per book ply: small + bounded (a recognised line, not a proven game).</summary>
    private static long OpeningGames =>
        Math.Clamp(long.TryParse(Environment.GetEnvironmentVariable("LAPLACE_OPENING_GAMES"), out var v) ? v : 4, 1, 64);

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.OpeningsSourceId, SourceName, ChessVocabulary.OpeningsTrustClass, ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (options.DryRun) yield break;

        int linesPerBatch = options.BatchSize > 1 ? options.BatchSize : LinesPerBatch;
        var modality = new ChessModality();
        foreach (var file in EnumerateFiles(context.EcosystemPath))
        {
            ct.ThrowIfCancellationRequested();
            var builder = NewBuilder(context);
            int inBatch = 0;
            await foreach (var row in StreamRowsAsync(file, ct).WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();

                var sans = ExtractSans(row.Movetext);
                if (sans.Count == 0) continue;
                AppendLine(builder, modality, sans, row.Eco, row.Name);   // a malformed token aborts that line only

                if (++inBatch >= linesPerBatch)
                {
                    yield return await builder.SetInputUnitsConsumed(inBatch).BuildAsync(ct);
                    IntentStage.ResetContentBank();
                    builder = NewBuilder(context);
                    inBatch = 0;
                }
            }
            if (inBatch > 0)
            {
                yield return await builder.SetInputUnitsConsumed(inBatch).BuildAsync(ct);
                IntentStage.ResetContentBank();
            }
        }
    }

    private static SubstrateChangeBuilder NewBuilder(IDecomposerContext ctx)
        // Deferred-content skip ON: opening prefixes are shared massively (every line through 1.e4 e5
        // collapses to the same nodes), so the probe stages repeated content once.
        => new SubstrateChangeBuilder(ChessVocabulary.OpeningsSourceId, "chess/openings").EnableDeferredContent(ctx.Reader);

    /// <summary>Replay one opening line's mainline, emitting a Draw-scored (book) MOVE edge per ply, then
    /// tag the FINAL position with the line's NAME + ECO code (the identifying value — "what opening is
    /// this?"). Aborts the line on an unresolved token (malformed/illegal SAN); the prefix already emitted
    /// stands.</summary>
    private static void AppendLine(SubstrateChangeBuilder b, ChessModality m, List<string> sans, string eco, string name)
    {
        long games = OpeningGames;
        var state = m.Initial();
        bool any = false;
        foreach (var san in sans)
        {
            var mv = San.Resolve(state.Board, m.LegalActions(state), san);
            if (mv is null) return;
            var next = m.Apply(state, mv.Value);
            // Openings are anonymous book theory: dedicated ChessOpenings source, NO named mover (null) —
            // book lines have no player, so no false PLAYED_BY attribution. Draw-scored = recognized book.
            ChessGraph.AppendMoveEdge(
                b, m.StateKey(state), m.StateKey(next), PlyOutcome.Draw, games, OpeningWitnessWeight,
                sourceId: ChessVocabulary.OpeningsSourceId, moverPlayerId: null);
            state = next;
            any = true;
        }
        if (!any) return;

        // Name/ECO capture: the final position IS the named line. The name + eco are content entities
        // (identical names across lines converge to one node), linked from the position. This is what lets
        // the substrate answer "this position is the Najdorf (B90)".
        var finalId = ChessCompose.PositionId(m.StateKey(state));
        if (!string.IsNullOrWhiteSpace(name) && ContentEmitter.Emit(b, name, ChessVocabulary.OpeningsSourceId) is { } nameId)
            b.AddAttestation(NativeAttestation.Categorical(
                finalId, "OPENING_NAME", nameId, ChessVocabulary.OpeningsSourceId, null, SourceTrust.AcademicCurated));
        if (!string.IsNullOrWhiteSpace(eco) && ContentEmitter.Emit(b, eco, ChessVocabulary.OpeningsSourceId) is { } ecoId)
            b.AddAttestation(NativeAttestation.Categorical(
                finalId, "HAS_ECO", ecoId, ChessVocabulary.OpeningsSourceId, null, SourceTrust.AcademicCurated));
    }

    /// <summary>Parse one TSV row's movetext into its ordered mainline SAN tokens via the <c>pgn</c>
    /// tree-sitter grammar — the exact same parser the game-PGN path uses.</summary>
    internal static List<string> ExtractSans(string movetext)
    {
        var bytes = Encoding.UTF8.GetBytes(movetext);
        using var ast = GrammarDecomposer.Parse(bytes, "pgn");
        return PgnMovetext.Extract(ast, bytes).Moves;
    }

    /// <summary>Split a TSV line into (eco, name, movetext); null for the header row or a malformed line.
    /// Pure string handling — no grammar, no DB (the unit-test seam).</summary>
    internal static (string Eco, string Name, string Movetext)? ParseRow(string line)
    {
        if (line.Length == 0) return null;
        var cols = line.Split('\t');
        if (cols.Length < 3) return null;
        string eco = cols[0].Trim(), name = cols[1].Trim(), movetext = cols[2].Trim();
        if (eco.Length == 0 || movetext.Length == 0) return null;
        if (string.Equals(eco, "eco", StringComparison.OrdinalIgnoreCase)) return null; // header
        return (eco, name, movetext);
    }

    private static async IAsyncEnumerable<(string Eco, string Name, string Movetext)> StreamRowsAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            if (ParseRow(line) is { } row)
                yield return row;
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long lines = 0;
        foreach (var f in EnumerateFiles(context.EcosystemPath))
        {
            try
            {
                using var r = new StreamReader(f);
                string? line;
                while ((line = await r.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                    if (ParseRow(line) is not null) lines++;
            }
            catch { /* skip unreadable */ }
        }
        return lines == 0 ? null : lines;
    }

    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IEnumerable<string> EnumerateFiles(string path)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        if (File.Exists(path)) { yield return Path.GetFullPath(path); yield break; }
        if (!Directory.Exists(path)) yield break;
        foreach (var f in Directory.EnumerateFiles(path, "*.tsv", SearchOption.AllDirectories)
                                   .OrderBy(p => p, StringComparer.Ordinal))
            yield return f;
    }
}
