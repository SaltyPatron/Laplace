using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

/// <summary>
/// Chess literature → board modality. Reads book text files (the Gutenberg chess corpus) and
/// grounds what the book asserts onto content-addressed board entities, under the reserved
/// ChessBook source (curated trust):
///
///  - Embedded PGN games (annotated game collections) are recorded through the same witnessed
///    shape as ChessPgnDecomposer — under ChessBook provenance — and picked up by the analyzer
///    scan for the calculated ladder. Their inline {commentary} is additionally attested
///    (comment, EXPLAINS, position-after-move): the book's judgment, tied to the exact position
///    it judges.
///  - Prose move lines — algebraic ("1. e4 e5 2. Nf3") or English descriptive ("1. P-K4, P-K4;
///    2. Kt-KB3") — are replayed from the standard start; lines that ground legally emit one
///    ordered line trajectory plus (paragraph, EXPLAINS, line). Fragments quoted from diagrams fail
///    replay from the start position and are skipped by construction: only deterministic
///    groundings are attested.
///
/// The paragraph/comment text is minted through the same content law as the document lane, so a
/// sentence already ingested as literature collides to the same id — the cross-modal mesh is a
/// hash collision, never a resolution pass.
/// </summary>
public sealed partial class ChessBookDecomposer(bool recursive = false)
    : ComposeDecomposer<ChessBookRecord>, IIngestInventoryProvider, IIngestNoOpExplainer
{
    private readonly SearchOption _scope =
        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

    public override Hash128 SourceId => ChessVocabulary.BookSourceId;
    public override string SourceName => "ChessBook";
    public override int LayerOrder => 20;
    public override Hash128 TrustClassId => ChessVocabulary.BookTrustClass;
    protected override double SourceTrust => TC.AcademicCurated;
    protected override string BatchLabelPrefix => "chess/book";

    internal const string DiagramAnchoredLine = "diagram-anchored-line";

    private const double BookWitnessWeight = 0.7;
    private const int MinProsePlies = 3;
    private const int MaxContextChars = 480;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.BookSourceId, SourceName, ChessVocabulary.BookTrustClass, ct);

    protected override async IAsyncEnumerable<ChessBookRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ChessDropLedger.Reset();
        try
        {
            foreach (var file in EnumerateFiles(ecosystemPath, _scope))
            {
                ct.ThrowIfCancellationRequested();
                string text = await File.ReadAllTextAsync(file, Encoding.UTF8, ct);
                var records = ExtractFromText(text, Path.GetFileNameWithoutExtension(file)).ToList();

                foreach (var record in await GateNoveltyAsync(records, options.ReObservePresent, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    yield return record;
                }
            }
        }
        finally { ChessDropLedger.Report(SourceName); }
    }

    private async Task<IReadOnlyList<ChessBookRecord>> GateNoveltyAsync(
        List<ChessBookRecord> records, bool reObservePresent, CancellationToken ct)
    {
        if (reObservePresent || ContainmentReader is not { } reader || records.Count == 0)
            return records;

        var probeIds = new List<Hash128>(records.Count * 2);
        var offsets = new (int Marker, int Game)[records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (r.Parsed is { } parsed)
            {
                offsets[i] = (probeIds.Count, probeIds.Count + 1);
                probeIds.Add(ChessVocabulary.AnalysisMarkerId(parsed.PlayingId, ChessAnalyze.Version));
                probeIds.Add(parsed.PlayingId);
            }
            else
            {
                offsets[i] = (probeIds.Count, -1);
                probeIds.Add(r.RootId);
            }
        }

        byte[] bm = await reader.EntitiesExistBitmapAsync(probeIds.ToArray(), ct).ConfigureAwait(false);
        bool Present(int k) => BitmapBits.IsSet(bm, k);

        var novel = new List<ChessBookRecord>(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            var (markerIdx, gameIdx) = offsets[i];
            if (Present(markerIdx)) continue;
            var r = records[i];
            novel.Add(gameIdx >= 0 && Present(gameIdx)
                ? r with { NeedsRecord = false }
                : r);
        }
        return novel;
    }

    protected override void Compose(ChessBookRecord record, SubstrateChangeBuilder b)
    {
        if (record.Parsed is not null) ComposeEmbeddedGame(record, b);
        else ComposeProseLine(record, b);
    }

    public override async Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default)
    {
        long units = 0;
        foreach (var f in EnumerateFiles(context.EcosystemPath, _scope))
        {
            try
            {
                string text = await File.ReadAllTextAsync(f, Encoding.UTF8, ct);
                foreach (var line in text.Split('\n'))
                    if (line.StartsWith("[Event ", StringComparison.Ordinal)) units++;
                units += LineAnchor().Matches(text).Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"ChessBookDecomposer: failed to estimate units in {f}: {ex.Message}");
            }
        }
        return units == 0 ? null : units;
    }

    private static void ComposeEmbeddedGame(ChessBookRecord record, SubstrateChangeBuilder b)
    {
        var parsed = record.Parsed!;
        var src = ChessVocabulary.BookSourceId;

        if (record.NeedsRecord)
        {
            ChessPgnDecomposer.RecordGame(parsed, b, src);

            if (!string.IsNullOrWhiteSpace(record.Context)
                && ContentEmitter.Emit(b, record.Context, src) is { } ctxId)
                b.AddAttestation(NativeAttestation.Categorical(
                    ctxId, "EXPLAINS", parsed.LineId, src, null, BookWitnessWeight));

            var m = new ChessModality();
            var state = m.Initial();
            var mainline = parsed.Walk.Mainline;
            for (int ply = 0; ply < mainline.Count; ply++)
            {
                var mv = San.Resolve(state.Board, m.LegalActions(state), mainline[ply].San);
                if (mv is null) break;
                state = m.Apply(state, mv.Value);

                string? comment = mainline[ply].CommentText;
                if (string.IsNullOrWhiteSpace(comment)) continue;
                var posId = ChessGraph.EmitPosition(b, state.Board, src);
                if (ContentEmitter.Emit(b, comment.Trim(), src) is { } commentId)
                    b.AddAttestation(NativeAttestation.Categorical(
                        commentId, "EXPLAINS", posId, src, parsed.PlayingId, BookWitnessWeight));
            }
        }

        ChessAnalyze.DeriveFromParsed(b, parsed);
    }

    private static void ComposeProseLine(ChessBookRecord record, SubstrateChangeBuilder b)
    {
        var src = ChessVocabulary.BookSourceId;
        b.AddEntity(record.RootId, EntityTier.Document, ChessVocabulary.BookLineType, src);

        var m = new ChessModality();
        var state = m.Initial();
        var states = new List<ChessState>(record.Sans.Count + 1) { state };
        var resolved = new List<(Piece Moving, ChessMove Move)>(record.Sans.Count);
        foreach (var san in record.Sans)
        {
            var mv = San.Resolve(state.Board, m.LegalActions(state), san);
            if (mv is null) return;
            resolved.Add((state.Board.Squares[mv.Value.From], mv.Value));
            state = m.Apply(state, mv.Value);
            states.Add(state);
        }

        b.AddEntity(record.LineId, EntityTier.Document, ChessVocabulary.GameType, src);
        var line = new List<ChessNode>(states.Count);
        foreach (var position in states)
            line.Add(ChessGraph.ComposePositionPoint(position.Board));
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var movePoints = resolved.Select(r =>
            ChessGraph.EmitMove(b, r.Moving, r.Move, src, nowUs)).ToArray();
        ChessGraph.AppendLineTrajectory(
            b, record.LineId, line[0], movePoints, src, nowUs);
        ChessGraph.AppendPositionProjection(
            b, record.LineId, line, src, nowUs);
        if (!string.IsNullOrWhiteSpace(record.Context)
            && ContentEmitter.Emit(b, record.Context, src) is { } ctxId)
            b.AddAttestation(NativeAttestation.Categorical(
                ctxId, "EXPLAINS", record.LineId, src, null, BookWitnessWeight));
    }

    internal static IEnumerable<ChessBookRecord> ExtractFromText(string text, string fallbackTitle)
    {
        string title = ExtractTitle(text) ?? fallbackTitle;
        var (pgnBlocks, remainder) = SplitEmbeddedPgn(text);
        foreach (var (gameText, context) in pgnBlocks)
        {
            if (ChessPgnDecomposer.TryParseGame(gameText) is not { } parsed) continue;
            yield return new ChessBookRecord(title, gameText, Array.Empty<string>(), context)
            {
                Parsed = parsed,
                RootId = parsed.PlayingId,
            };
        }

        var titleContentId = ContentEmitter.RootId(title);
        foreach (var paragraph in Paragraphs(remainder))
        {
            foreach (var sans in ExtractProseLines(paragraph))
            {
                if (titleContentId is null
                    || ChessPgnDecomposer.TryReplayLineDetailed(sans, startFen: null) is not { } replay)
                {
                    ChessDropLedger.Drop(DiagramAnchoredLine, string.Join(' ', sans.Take(8)));
                    continue;
                }
                ChessDropLedger.Kept();
                var lineId = ChessCompose.LineId(replay.PositionIds[0], replay.MoveIds);
                yield return new ChessBookRecord(title, null, sans, TrimContext(paragraph))
                {
                    LineId = lineId,
                    RootId = ChessVocabulary.BookLineMarkerId(titleContentId.Value, lineId),
                };
            }
        }
    }

    private static string? ExtractTitle(string text)
    {
        var match = TitleLine().Match(text.Length > 4096 ? text[..4096] : text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private const int MaxPgnBlockLines = 512;

    internal static (List<(string GameText, string Context)> Blocks, string Remainder) SplitEmbeddedPgn(string text)
    {
        var blocks = new List<(string, string)>();
        var remainder = new StringBuilder(text.Length);
        var lines = text.Split('\n');

        int i = 0;
        var prose = new List<string>();
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("[Event ", StringComparison.Ordinal))
            {
                prose.Add(lines[i]);
                remainder.Append(lines[i]).Append('\n');
                i++;
                continue;
            }

            string context = PrecedingParagraph(prose);
            var blockLines = new List<string>();
            bool sawResult = false;
            while (i < lines.Length && blockLines.Count < MaxPgnBlockLines)
            {
                string line = lines[i];
                if (sawResult && (line.StartsWith("[Event ", StringComparison.Ordinal)
                                  || string.IsNullOrWhiteSpace(line)))
                    break;
                blockLines.Add(line);
                i++;
                if (!line.StartsWith("[", StringComparison.Ordinal) && ResultToken().IsMatch(line))
                    sawResult = true;
            }

            if (sawResult)
            {
                blocks.Add((string.Join('\n', blockLines) + "\n", context));
            }
            else
            {
                System.Diagnostics.Trace.TraceWarning(
                    "ChessBookDecomposer: [Event block without result token ({0} lines) returned to prose",
                    blockLines.Count);
                foreach (var line in blockLines)
                {
                    prose.Add(line);
                    remainder.Append(line).Append('\n');
                }
            }
        }

        return (blocks, remainder.ToString());
    }

    private static string PrecedingParagraph(List<string> proseLines)
    {
        int end = proseLines.Count;
        while (end > 0 && string.IsNullOrWhiteSpace(proseLines[end - 1])) end--;
        int start = end;
        while (start > 0 && !string.IsNullOrWhiteSpace(proseLines[start - 1])) start--;
        return TrimContext(string.Join(' ', proseLines[start..end]).Trim());
    }

    private static string TrimContext(string s)
    {
        s = Whitespace().Replace(s, " ").Trim();
        return s.Length <= MaxContextChars ? s : s[^MaxContextChars..];
    }

    private static IEnumerable<string> Paragraphs(string text)
    {
        foreach (var p in ParagraphSplit().Split(text))
        {
            var t = p.Trim();
            if (t.Length > 0) yield return t;
        }
    }

    internal static IEnumerable<IReadOnlyList<string>> ExtractProseLines(string paragraph)
    {
        bool descriptive = DescriptiveMarker().IsMatch(paragraph);
        int searchFrom = 0;
        while (searchFrom < paragraph.Length)
        {
            var anchor = LineAnchor().Match(paragraph, searchFrom);
            if (!anchor.Success) yield break;

            int consumedTo;
            var sans = descriptive
                ? ReplayDescriptive(paragraph, anchor.Index, out consumedTo)
                : ReplayAlgebraic(paragraph, anchor.Index, out consumedTo);

            if (sans.Count >= MinProsePlies) yield return sans;
            searchFrom = Math.Max(consumedTo, anchor.Index + anchor.Length);
        }
    }

    private static List<string> ReplayAlgebraic(string paragraph, int from, out int consumedTo)
    {
        var m = new ChessModality();
        var state = m.Initial();
        var sans = new List<string>();
        consumedTo = from;

        foreach (Match tok in Token().Matches(paragraph[from..]))
        {
            string raw = MoveNumberPrefix().Replace(tok.Value, "");
            if (raw.Length == 0) { consumedTo = from + tok.Index + tok.Length; continue; }

            string cleaned = raw.Trim('(', ')').TrimEnd(',', ';', ':', '.', '!', '?');
            var legal = m.LegalActions(state);
            var mv = San.Resolve(state.Board, legal, cleaned);
            if (mv is null) break;
            sans.Add(San.ToSan(state.Board, mv.Value));
            state = m.Apply(state, mv.Value);
            consumedTo = from + tok.Index + tok.Length;
        }
        return sans;
    }

    private static List<string> ReplayDescriptive(string paragraph, int from, out int consumedTo)
    {
        var m = new ChessModality();
        var state = m.Initial();
        var sans = new List<string>();
        consumedTo = from;

        int pos = from;
        foreach (var segment in SegmentSplit().Split(paragraph[from..]))
        {
            int segStart = pos;
            pos += segment.Length + 1;
            string item = TrimToMoveWords(MoveNumberPrefix().Replace(segment, "").Trim());
            if (item.Length == 0) continue;

            var legal = m.LegalActions(state);
            var mv = DescriptiveNotation.Resolve(state.Board, legal, item);
            if (mv is null)
            {
                if (sans.Count > 0) break;
                continue;
            }
            sans.Add(San.ToSan(state.Board, mv.Value));
            state = m.Apply(state, mv.Value);
            consumedTo = Math.Min(paragraph.Length, segStart + segment.Length);
        }
        return sans;
    }

    private static string TrimToMoveWords(string item)
    {
        var words = item.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int keep = 0;
        while (keep < words.Length && MoveWord().IsMatch(words[keep])) keep++;
        return string.Join(' ', words[..keep]);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateFiles(context.EcosystemPath, _scope);
        if (options.MaxInputUnits > 0)
            return Task.FromResult(IngestInventory.FromFiles("assertions", paths, options.MaxInputUnits, ct));

        var files = new List<IngestFileSpec>(paths.Count);
        long total = 0;
        foreach (var p in paths)
        {
            long n = CountCandidateAssertions(p, ct);
            files.Add(new IngestFileSpec(Path.GetFileName(p), p, n));
            total += n;
        }
        return Task.FromResult<IngestInventory?>(new IngestInventory("assertions", total, files));
    }

    private static long CountCandidateAssertions(string path, CancellationToken ct)
    {
        try
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            ct.ThrowIfCancellationRequested();
            return ExtractFromText(text, Path.GetFileNameWithoutExtension(path)).LongCount();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"ChessBookDecomposer: failed to inventory {path}: {ex.Message}");
            return 0;
        }
    }

    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
        => ChessDropLedger.ExplainEmptyRun(SourceName, declaredInputUnits);

    private static IReadOnlyList<string> EnumerateFiles(string path, SearchOption scope)
        => ChessInput.Resolve(path, scope, ChessInput.BookExtensions, "chess-books");

    [GeneratedRegex(@"^Title:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex TitleLine();

    [GeneratedRegex(@"(?<!\S)(?:1-0|0-1|1/2-1/2|\*)(?!\S)")]
    private static partial Regex ResultToken();

    [GeneratedRegex(@"\r?\n\s*\r?\n")]
    private static partial Regex ParagraphSplit();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<![\d.])1\s*\.{0,3}\s*(?=[a-hPKQRBNO0])")]
    private static partial Regex LineAnchor();

    [GeneratedRegex(@"\b(?:Kt|KT|[PRNBQK])\s*[-–—]\s*(?:Q|K)?\s*(?:Kt|KT|[RNB])?\s*[1-8]|\b(?:Kt|[PRNBQK])x(?:Kt|[PRNBQK])")]
    private static partial Regex DescriptiveMarker();

    [GeneratedRegex(@"\S+")]
    private static partial Regex Token();

    [GeneratedRegex(@"[,;]|\s{2,}")]
    private static partial Regex SegmentSplit();

    [GeneratedRegex(@"^[PKQRBNKtO0-8xX+#=/()\-.]+$|^(?i:castles|ch|dis|dbl|mate|ep|e\.p\.?)$")]
    private static partial Regex MoveWord();

    [GeneratedRegex(@"^\s*\(?\d{1,3}\s*\.{0,3}\s*")]
    private static partial Regex MoveNumberPrefix();
}

public sealed record ChessBookRecord(
    string BookTitle,
    string? GameText,
    IReadOnlyList<string> Sans,
    string Context) : ITrunkRootRecord
{
    internal ChessGameRecord? Parsed { get; init; }
    internal Hash128 RootId { get; init; }
    internal Hash128 LineId { get; init; }
    internal bool NeedsRecord { get; init; } = true;
    public Hash128 TrunkRootId => RootId;
}
