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

public sealed class ChessOpeningsDecomposer(bool recursive = false)
    : ComposeDecomposer<ChessOpeningRecord>, IIngestInventoryProvider, IIngestNoOpExplainer
{
    private readonly SearchOption _scope =
        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

    public override Hash128 SourceId => ChessVocabulary.OpeningsSourceId;
    public override string SourceName => "ChessOpenings";
    public override int LayerOrder => 20;
    public override Hash128 TrustClassId => ChessVocabulary.OpeningsTrustClass;
    protected override double SourceTrust => TC.AcademicCurated;
    protected override string BatchLabelPrefix => "chess/openings";

    private const double OpeningWitnessWeight = 0.7;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.OpeningsSourceId, SourceName, ChessVocabulary.OpeningsTrustClass, ct);

    protected override async IAsyncEnumerable<ChessOpeningRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ChessDropLedger.Reset();
        try
        {
            foreach (var file in EnumerateFiles(ecosystemPath, _scope))
            {
                await foreach (var row in StreamRowsAsync(file, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    var sans = ExtractSans(row.Movetext);
                    if (sans.Count == 0)
                    {
                        ChessDropLedger.Drop(ChessDropLedger.NoResultOrMoves, $"{row.Eco} {row.Name} :: {row.Movetext}");
                        continue;
                    }
                    ChessDropLedger.Kept();
                    yield return new ChessOpeningRecord(row.Eco, row.Name, sans);
                }
            }
        }
        finally { ChessDropLedger.Report(SourceName); }
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
                    $"ChessOpeningsDecomposer: failed to estimate rows in {f}: {ex.Message}");
            }
        }
        return lines == 0 ? null : lines;
    }

    /// <summary>
    /// Catalog unit is the LINE (GH #736 / Chess catalog dual): Merkle of start position and ordered move
    /// ids, trajectory physicality, OPENING_NAME / HAS_ECO on the line. Ordered transitions
    /// are read from the trajectory rather than restated as fabricated draw testimony.
    /// The terminal board is recovered from the line trajectory by the catalog reader; it is
    /// structure, not a second source assertion on an exact-position SQL entity.
    /// </summary>
    private static void AppendLine(SubstrateChangeBuilder b, ChessModality m, List<string> sans, string eco, string name)
    {
        var state = m.Initial();
        var line = new List<ChessNode>(sans.Count + 1);
        var moves = new List<ChessNode>(sans.Count);
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

        lock (ChessCompose.Gate)
        {
            line.Add(ChessGraph.ComposePositionPoint(state.Board));
            foreach (var san in sans)
            {
                var mv = San.Resolve(state.Board, m.LegalActions(state), san);
                if (mv is null) return;
                moves.Add(ChessGraph.ComposeMovePoint(
                    state.Board.Squares[mv.Value.From], mv.Value));
                state = m.Apply(state, mv.Value);
                line.Add(ChessGraph.ComposePositionPoint(state.Board));
            }
        }
        if (line.Count < 2) return;

        var moveIds = moves.Select(static move => move.Id).ToArray();
        var lineId = ChessCompose.LineId(line[0].Id, moveIds);

        b.AddEntity(lineId, EntityTier.Document, ChessVocabulary.GameType, ChessVocabulary.OpeningsSourceId);
        ChessGraph.AppendLineTrajectory(
            b, lineId, line[0], moves, ChessVocabulary.OpeningsSourceId, nowUs);
        ChessGraph.AppendPositionProjection(b, lineId, line, ChessVocabulary.OpeningsSourceId, nowUs);

        Hash128? nameId = null;
        Hash128? ecoId = null;
        if (!string.IsNullOrWhiteSpace(name))
            nameId = ContentEmitter.Emit(b, name, ChessVocabulary.OpeningsSourceId);
        if (!string.IsNullOrWhiteSpace(eco))
            ecoId = ContentEmitter.Emit(b, eco, ChessVocabulary.OpeningsSourceId);

        if (nameId is { } nid)
            b.AddAttestation(NativeAttestation.Categorical(
                lineId, ChessSeedManifest.OpeningName, nid, ChessVocabulary.OpeningsSourceId, null, TC.AcademicCurated));
        if (ecoId is { } eid)
            b.AddAttestation(NativeAttestation.Categorical(
                lineId, ChessSeedManifest.HasEco, eid, ChessVocabulary.OpeningsSourceId, null, TC.AcademicCurated));
    }

    internal static SubstrateChange ComposeLineForTest(string eco, string name, IReadOnlyList<string> sans)
    {
        var b = new SubstrateChangeBuilder(ChessVocabulary.OpeningsSourceId, "test/openings")
            .DeclareSourcePrior(TC.AcademicCurated);
        AppendLine(b, new ChessModality(), sans.ToList(), eco, name);
        return b.SetInputUnitsConsumed(1).Build();
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

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateFiles(context.EcosystemPath, _scope);
        if (options.MaxInputUnits > 0)
            return Task.FromResult(IngestInventory.FromFiles("rows", paths, options.MaxInputUnits, ct));

        var files = new List<IngestFileSpec>(paths.Count);
        long total = 0;
        foreach (var p in paths)
        {
            long n = CountParsableRows(p, ct);
            files.Add(new IngestFileSpec(Path.GetFileName(p), p, n));
            total += n;
        }
        return Task.FromResult<IngestInventory?>(new IngestInventory("rows", total, files));
    }

    private static long CountParsableRows(string path, CancellationToken ct)
    {
        long n = 0;
        foreach (var line in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();
            if (ParseRow(line) is not null) n++;
        }
        return n;
    }

    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
        => ChessDropLedger.ExplainEmptyRun(SourceName, declaredInputUnits);

    private static IReadOnlyList<string> EnumerateFiles(string path, SearchOption scope)
        => ChessInput.Resolve(path, scope, ChessInput.OpeningsExtensions, "openings");
}

public readonly record struct ChessOpeningRecord(string Eco, string Name, List<string> Sans);
