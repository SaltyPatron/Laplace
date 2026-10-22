using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

/// <summary>
/// Provider for chess book text files. What a book asserts is grounded onto content-addressed
/// board entities and attested under the ChessBook source (curated trust):
///
///  - Embedded PGN games are recorded through ChessPgnDecomposer.RecordGame, as any game is.
///    Inline {commentary} is also attested as (comment, EXPLAINS, position after the move).
///  - Prose move lines, algebraic ("1. e4 e5 2. Nf3") or descriptive ("1. P-K4, P-K4;
///    2. Kt-KB3"), replay from the standard start. A diagram FEN, or a move number that
///    continues a line already replayed in the paragraph, is the anchor when the standard
///    start cannot play the line. A replayed line emits its ordered trajectory. The paragraph
///    attests EXPLAINS on that trajectory and on each chess concept it names. A fragment that
///    does not replay, and had no anchor, is counted as a diagram-anchored drop. A line that
///    had an anchor and still did not replay is a different refusal.
///
/// Paragraph and comment text compose through the shared content recipe, so a sentence admitted
/// from any other source is the same entity.
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
    internal const string AnchoredLineUnreplayable = "anchored-line-unreplayable";

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

        var keys = records.Select(static record => record.CompletionKey).ToArray();
        var present = await reader.CompletedUnitsAsync(keys, ct).ConfigureAwait(false);
        var novel = new List<ChessBookRecord>(records.Count);
        for (int i = 0; i < records.Count; i++)
            if (!present.Contains(keys[i])) novel.Add(records[i]);
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
        var replay = ChessPgnDecomposer.MaterializeParsedReplay(parsed);
        if (!replay.IsCompleteFor(parsed)) return;

        ChessPgnDecomposer.RecordGame(parsed, b, src);
        if (!string.IsNullOrWhiteSpace(record.Context))
        {
            var ctxId = ContentEmitter.Emit(b, record.Context, src)
                ?? throw new InvalidDataException("book context could not be admitted as content");
            b.AddAttestation(NativeAttestation.Categorical(
                ctxId, "EXPLAINS", parsed.LineId, src, null, BookWitnessWeight));
            ExplainNamedConcepts(b, ctxId, record.Context, src);
        }

        var mainline = parsed.Walk.Mainline;
        for (int ply = 0; ply < mainline.Count; ply++)
        {
            string? comment = mainline[ply].CommentText;
            if (string.IsNullOrWhiteSpace(comment)) continue;
            // Positions come from the validated replay, which starts from the game's
            // SetUp/FEN board, not from the standard start.
            var posId = ChessGraph.EmitPosition(b, replay.Boards[ply + 1], src);
            var commentId = ContentEmitter.Emit(b, comment.Trim(), src)
                ?? throw new InvalidDataException("book commentary could not be admitted as content");
            b.AddAttestation(NativeAttestation.Categorical(
                commentId, "EXPLAINS", posId, src, parsed.PlayingId, BookWitnessWeight));
            ExplainNamedConcepts(b, commentId, comment.Trim(), src);
        }

        if (!ChessAnalyze.DeriveFromParsed(b, parsed, replay)) return;
        IngestUnitCompletion.Emit(b, record.RootId, src, 20, record.CompletionContextId);
    }

    private static void ComposeProseLine(ChessBookRecord record, SubstrateChangeBuilder b)
    {
        var src = ChessVocabulary.BookSourceId;
        b.AddEntity(record.RootId, EntityTier.Document, ChessVocabulary.BookLineType);

        var m = new ChessModality();
        ChessState state;
        try
        {
            state = string.IsNullOrWhiteSpace(record.StartFen)
                ? m.Initial()
                : m.FromFen(record.StartFen);
        }
        catch (FormatException)
        {
            return;
        }
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

        b.AddEntity(record.LineId, ChessCompose.LineFloor(record.Sans.Count), ChessVocabulary.GameType);
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
        if (!string.IsNullOrWhiteSpace(record.Context))
        {
            var ctxId = ContentEmitter.Emit(b, record.Context, src)
                ?? throw new InvalidDataException("book context could not be admitted as content");
            b.AddAttestation(NativeAttestation.Categorical(
                ctxId, "EXPLAINS", record.LineId, src, null, BookWitnessWeight));
            ExplainNamedConcepts(b, ctxId, record.Context, src);
        }
        IngestUnitCompletion.Emit(b, record.RootId, src, 20, record.CompletionContextId);
    }

    // The paragraph explains the concept, and the book-domain member is the context that
    // selects that sense. Membership is IS_EXAMPLE_OF, which is already a declared relation.
    private static void ExplainNamedConcepts(
        SubstrateChangeBuilder b, Hash128 proseId, string prose, Hash128 src)
    {
        foreach (var concept in ChessConcepts.All)
        {
            if (!concept.MatchesBook(prose)) continue;
            var member = concept.Member(ChessConcepts.Domain.Book);
            b.AddEntity(concept.Id, EntityTier.Document, ChessVocabulary.AnalysisMarkerType);
            b.AddEntity(member, EntityTier.Document, ChessVocabulary.AnalysisMarkerType);
            b.AddAttestation(NativeAttestation.Categorical(
                member, "IS_EXAMPLE_OF", concept.Id, src, null, BookWitnessWeight));
            b.AddAttestation(NativeAttestation.Categorical(
                proseId, "EXPLAINS", concept.Id, src, member, BookWitnessWeight));
        }
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
            foreach (var anchored in ExtractAnchoredLines(paragraph))
            {
                if (titleContentId is null
                    || ChessPgnDecomposer.TryReplayLineDetailed(anchored.Sans, anchored.StartFen) is not { } replay)
                {
                    string detail = string.Join(' ', anchored.Sans.Take(8));
                    ChessDropLedger.Drop(
                        anchored.StartFen is null ? DiagramAnchoredLine : AnchoredLineUnreplayable,
                        detail);
                    continue;
                }
                ChessDropLedger.Kept();
                var lineId = ChessCompose.LineId(replay.PositionIds[0], replay.MoveIds);
                yield return new ChessBookRecord(title, null, anchored.Sans, TrimContext(paragraph))
                {
                    LineId = lineId,
                    RootId = ChessVocabulary.BookLineMarkerId(titleContentId.Value, lineId),
                    StartFen = anchored.StartFen,
                };
            }
        }
    }

    internal readonly record struct AnchoredProseLine(IReadOnlyList<string> Sans, string? StartFen);

    /// <summary>
    /// Lines that replay from the standard start, from a diagram FEN earlier in the paragraph,
    /// or from the position a previous line in the paragraph had reached when the move number says so.
    /// </summary>
    internal static IEnumerable<AnchoredProseLine> ExtractAnchoredLines(string paragraph)
    {
        var modality = new ChessModality();
        bool descriptive = DescriptiveMarker().IsMatch(paragraph);
        var fens = new List<(int Index, string Fen, bool FullmoveWritten)>();
        foreach (Match fen in DiagramFen().Matches(paragraph))
        {
            if (TryFen(modality, fen.Groups[1].Value) is null) continue;
            fens.Add((fen.Index, fen.Groups[1].Value.Trim(), fen.Groups[2].Success));
        }

        int consumedTo = 0;
        var kept = new List<(IReadOnlyList<string> Sans, string? StartFen, ChessState End)>();
        foreach (Match anchor in AnyMoveAnchor().Matches(paragraph))
        {
            if (anchor.Index < consumedTo) continue;
            if (fens.Exists(f => anchor.Index >= f.Index && anchor.Index < f.Index + f.Fen.Length))
                continue;
            int number = int.Parse(anchor.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            int dots = 0;
            foreach (char c in anchor.Groups[2].Value)
                if (c == '.') dots++;
            bool blackToMove = dots >= 2;
            bool numberedContinuation = number != 1 || blackToMove;
            string? preceding = null;
            bool fullmoveWritten = false;
            for (int i = fens.Count - 1; i >= 0; i--)
            {
                if (fens[i].Index < anchor.Index)
                {
                    preceding = fens[i].Fen;
                    fullmoveWritten = fens[i].FullmoveWritten;
                    break;
                }
            }

            List<string>? sans = null;
            string? startFen = null;
            ChessState? end = null;
            int played = 0;
            int used = anchor.Index + anchor.Length;

            // A diagram FEN is the anchor when the moves replay from it. A numbered
            // continuation only takes that FEN when the side (and the written fullmove,
            // if the diagram has one) agrees, so an earlier diagram does not steal a
            // later continuation of a line that was already replayed.
            if (preceding is not null && TryFen(modality, preceding) is { } fenState
                && FenAgrees(fenState, fullmoveWritten, numberedContinuation, blackToMove, number))
            {
                var fromFen = ReplayMoves(paragraph, anchor.Index, fenState, descriptive, out used);
                played = fromFen.Count;
                if (fromFen.Count >= MinProsePlies)
                {
                    sans = fromFen;
                    startFen = preceding;
                    end = EndState(modality, preceding, fromFen);
                }
            }

            if (sans is null && numberedContinuation)
            {
                foreach (var prior in kept)
                {
                    if (prior.End.Board.WhiteToMove == blackToMove) continue;
                    if (prior.End.Board.FullmoveNumber != number) continue;
                    string fen = prior.End.Board.ToFen();
                    var cont = ReplayMoves(paragraph, anchor.Index, prior.End, descriptive, out int contUsed);
                    used = Math.Max(used, contUsed);
                    played = Math.Max(played, cont.Count);
                    if (cont.Count < MinProsePlies) continue;
                    sans = cont;
                    startFen = fen;
                    end = EndState(modality, fen, cont);
                    break;
                }
            }

            if (sans is null && !numberedContinuation)
            {
                var fromStart = ReplayMoves(paragraph, anchor.Index, modality.Initial(), descriptive, out int startUsed);
                used = Math.Max(used, startUsed);
                played = Math.Max(played, fromStart.Count);
                if (fromStart.Count >= MinProsePlies)
                {
                    sans = fromStart;
                    end = EndState(modality, null, fromStart);
                }
            }

            consumedTo = Math.Max(consumedTo, used);
            if (sans is null || end is null)
            {
                if (played > 0) continue;
                ChessDropLedger.Drop(
                    preceding is not null || numberedContinuation
                        ? AnchoredLineUnreplayable
                        : DiagramAnchoredLine,
                    paragraph.Substring(anchor.Index, Math.Min(80, paragraph.Length - anchor.Index)));
                continue;
            }

            kept.Add((sans, startFen, end));
            yield return new AnchoredProseLine(sans, startFen);
        }
    }

    private static bool FenAgrees(
        ChessState state, bool fullmoveWritten, bool numberedContinuation, bool blackToMove, int number)
    {
        if (!numberedContinuation) return true;
        if (state.Board.WhiteToMove == blackToMove) return false;
        return !fullmoveWritten || state.Board.FullmoveNumber == number;
    }

    private static ChessState? TryFen(ChessModality modality, string fen)
    {
        try { return modality.FromFen(fen.Trim()); }
        catch (FormatException) { return null; }
    }

    private static ChessState EndState(ChessModality modality, string? startFen, IReadOnlyList<string> sans)
    {
        var state = startFen is null ? modality.Initial() : modality.FromFen(startFen);
        foreach (var san in sans)
        {
            var mv = San.Resolve(state.Board, modality.LegalActions(state), san);
            if (mv is null) return state;
            state = modality.Apply(state, mv.Value);
        }
        return state;
    }

    private static List<string> ReplayMoves(
        string paragraph, int from, ChessState state, bool descriptive, out int consumedTo)
        => descriptive
            ? ReplayDescriptive(paragraph, from, state, out consumedTo)
            : ReplayAlgebraic(paragraph, from, state, out consumedTo);

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
        return ReplayAlgebraic(paragraph, from, m.Initial(), out consumedTo);
    }

    private static List<string> ReplayAlgebraic(
        string paragraph, int from, ChessState state, out int consumedTo)
    {
        var m = new ChessModality();
        var sans = new List<string>();
        consumedTo = from;

        foreach (Match tok in Token().Matches(paragraph[from..]))
        {
            string raw = MoveNumberPrefix().Replace(tok.Value, "");
            if (raw.Length == 0) { consumedTo = from + tok.Index + tok.Length; continue; }

            string cleaned = raw.Trim('(', ')').TrimEnd(',', ';', ':', '.', '!', '?');
            if (sans.Count == 0 && cleaned is "..." or ".." or "." or "…")
            {
                consumedTo = from + tok.Index + tok.Length;
                continue;
            }
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
        return ReplayDescriptive(paragraph, from, m.Initial(), out consumedTo);
    }

    private static List<string> ReplayDescriptive(
        string paragraph, int from, ChessState state, out int consumedTo)
    {
        var m = new ChessModality();
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

    [GeneratedRegex(@"(?<![\d.])(\d{1,3})\s*((?:\.\s*){1,3}|\s+)(?=[a-hPKQRBNO0])")]
    private static partial Regex AnyMoveAnchor();

    [GeneratedRegex(@"\b((?:[pnbrqkPNBRQK1-8]+/){7}[pnbrqkPNBRQK1-8]+\s+[wb]\s+(?:-|K?Q?k?q?|[A-Ha-h]{1,4})\s+(?:-|[a-h][36])(?:\s+\d+(?:\s+(\d+))?)?)")]
    private static partial Regex DiagramFen();

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
    string Context) : ITrunkRootRecord, IIngestCompletionRecord
{
    internal ChessGameRecord? Parsed { get; init; }
    internal Hash128 RootId { get; init; }
    internal Hash128 LineId { get; init; }
    internal string? StartFen { get; init; }
    // One root can be quoted with different commentary, so the completion key also binds
    // both text inputs; playing, line, paragraph and testimony identities are unchanged.
    internal Hash128 CompletionContextId => Hash128.Text(
        $"chess/book-completion-input/v1/{RootId}/{ContentEmitter.RootId(Context)}/{ContentEmitter.RootId(GameText ?? string.Empty)}");
    internal IngestUnitCompletionKey CompletionKey => IngestUnitCompletion.Key(
        RootId, ChessVocabulary.BookSourceId, 20, CompletionContextId);
    public IngestUnitCompletionKey? Completion => CompletionKey;
    public Hash128 TrunkRootId => RootId;
}
