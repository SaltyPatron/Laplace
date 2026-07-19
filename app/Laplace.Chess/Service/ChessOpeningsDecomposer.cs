using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

public sealed class ChessOpeningsDecomposer(bool recursive = false) : ComposeDecomposer<ChessOpeningRecord>
{
    private readonly SearchOption _scope =
        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

    public override Hash128 SourceId => ChessVocabulary.OpeningsSourceId;
    public override string SourceName => "ChessOpenings";
    public override int LayerOrder => 20;
    public override Hash128 TrustClassId => ChessVocabulary.OpeningsTrustClass;
    protected override double SourceTrust => TC.AcademicCurated;
    protected override string BatchLabelPrefix => "chess/openings";
    protected override int DefaultBatchSize => BatchConfigDefaults.ChessOpening;

    private const double OpeningWitnessWeight = 0.7;

    // One game's weight per book line (doc 03 C09): a catalog line asserts the line EXISTS, not
    // an outcome — at games=4 the fabricated Draw mass systematically dragged sharp book lines
    // toward neutral against real-game evidence. games=1 keeps the existence witness while real
    // outcomes dominate as soon as any actual games fold in.
    private static long OpeningGames => 1;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.OpeningsSourceId, SourceName, ChessVocabulary.OpeningsTrustClass, ct);

    protected override async IAsyncEnumerable<ChessOpeningRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var file in EnumerateFiles(ecosystemPath, _scope))
        {
            await foreach (var row in StreamRowsAsync(file, ct))
            {
                ct.ThrowIfCancellationRequested();
                var sans = ExtractSans(row.Movetext);
                if (sans.Count == 0) continue;
                yield return new ChessOpeningRecord(row.Eco, row.Name, sans);
            }
        }
    }

    protected override void Compose(ChessOpeningRecord record, SubstrateChangeBuilder b)
    {
        var modality = new ChessModality();
        AppendLine(b, modality, record.Sans, record.Eco, record.Name);
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long lines = 0;
        foreach (var f in EnumerateFiles(context.EcosystemPath, _scope))
        {
            try
            {
                using var r = new StreamReader(f);
                string? line;
                while ((line = await r.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                    if (ParseRow(line) is not null) lines++;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "ChessOpeningsDecomposer: failed to estimate rows in {File}: {Message}", f, ex.Message);
            }
        }
        return lines == 0 ? null : lines;
    }

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
                sourceId: ChessVocabulary.OpeningsSourceId);
            state = next;
            any = true;
        }
        if (!any) return;

        var finalId = ChessCompose.PositionId(m.StateKey(state));
        if (!string.IsNullOrWhiteSpace(name) && ContentEmitter.Emit(b, name, ChessVocabulary.OpeningsSourceId) is { } nameId)
            b.AddAttestation(NativeAttestation.Categorical(
                finalId, "OPENING_NAME", nameId, ChessVocabulary.OpeningsSourceId, null, TC.AcademicCurated));
        if (!string.IsNullOrWhiteSpace(eco) && ContentEmitter.Emit(b, eco, ChessVocabulary.OpeningsSourceId) is { } ecoId)
            b.AddAttestation(NativeAttestation.Categorical(
                finalId, "HAS_ECO", ecoId, ChessVocabulary.OpeningsSourceId, null, TC.AcademicCurated));
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

    private static IEnumerable<string> EnumerateFiles(string path, SearchOption scope)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        if (File.Exists(path)) { yield return Path.GetFullPath(path); yield break; }
        if (!Directory.Exists(path)) yield break;
        foreach (var f in Directory.EnumerateFiles(path, "*.tsv", scope)
                                   .OrderBy(p => p, StringComparer.Ordinal))
            yield return f;
    }
}

public readonly record struct ChessOpeningRecord(string Eco, string Name, List<string> Sans);
