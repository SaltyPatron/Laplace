using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

public sealed class ChessOpeningsDecomposer : IDecomposer
{
    public Hash128 SourceId => ChessVocabulary.OpeningsSourceId;
    public string SourceName => "ChessOpenings";
    public int LayerOrder => 20;
    public Hash128 TrustClassId => ChessVocabulary.OpeningsTrustClass;

    private const int LinesPerBatch = 256;
    private const double OpeningWitnessWeight = 0.7;

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
                AppendLine(builder, modality, sans, row.Eco, row.Name);

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


        => new SubstrateChangeBuilder(ChessVocabulary.OpeningsSourceId, "chess/openings");

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


            ChessGraph.AppendMoveEdge(
                b, m.StateKey(state), m.StateKey(next), PlyOutcome.Draw, games, OpeningWitnessWeight,
                sourceId: ChessVocabulary.OpeningsSourceId, moverPlayerId: null);
            state = next;
            any = true;
        }
        if (!any) return;




        var finalId = ChessCompose.PositionId(m.StateKey(state));
        if (!string.IsNullOrWhiteSpace(name) && ContentEmitter.Emit(b, name, ChessVocabulary.OpeningsSourceId) is { } nameId)
            b.AddAttestation(NativeAttestation.Categorical(
                finalId, "OPENING_NAME", nameId, ChessVocabulary.OpeningsSourceId, null, SourceTrust.AcademicCurated));
        if (!string.IsNullOrWhiteSpace(eco) && ContentEmitter.Emit(b, eco, ChessVocabulary.OpeningsSourceId) is { } ecoId)
            b.AddAttestation(NativeAttestation.Categorical(
                finalId, "HAS_ECO", ecoId, ChessVocabulary.OpeningsSourceId, null, SourceTrust.AcademicCurated));
    }

    internal static List<string> ExtractSans(string movetext)
    {
        var bytes = Encoding.UTF8.GetBytes(movetext);
        using var ast = GrammarDecomposer.Parse(bytes, "pgn");
        return PgnMovetext.Extract(ast, bytes).Moves;
    }

    internal static (string Eco, string Name, string Movetext)? ParseRow(string line)
    {
        if (line.Length == 0) return null;
        var cols = line.Split('\t');
        if (cols.Length < 3) return null;
        string eco = cols[0].Trim(), name = cols[1].Trim(), movetext = cols[2].Trim();
        if (eco.Length == 0 || movetext.Length == 0) return null;
        if (string.Equals(eco, "eco", StringComparison.OrdinalIgnoreCase)) return null;
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
            catch { }
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
