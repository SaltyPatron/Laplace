using System.Linq;
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

// Non-recursive by default: pointing at Games\Chess must not silently swallow every nested
// corpus (Lumbras\otb, fetch outputs). Recursion is an explicit operator decision
// (laplace ingest chess <dir> --recursive).
public sealed class ChessPgnDecomposer(bool recursive = false, bool analyzeInline = true)
    : ComposeDecomposer<ChessGameRecord>, IIngestInventoryProvider
{
    private readonly SearchOption _scope =
        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

    // GH #600: derive the calculated layer in the same Compose pass as the witnessed record,
    // reusing the in-memory parse. false (via `chess --no-analyze`) records game-grain only and
    // defers derivation to a later `chess-analyze` backfill — the pre-fusion two-step, kept as an
    // opt-out for fast record-only ingest.
    private readonly bool _analyzeInline = analyzeInline;

    public override Hash128 SourceId => ChessVocabulary.PgnSourceId;
    public override string SourceName => "ChessPgn";
    public override int LayerOrder => 20;
    public override Hash128 TrustClassId => ChessVocabulary.PgnTrustClass;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "chess/pgn";
    protected override int DefaultBatchSize => BatchConfigDefaults.Chess;

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessPgn.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessPgn.EstComposeUnitsPerRecord;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        // TWO sources, because the fused pass (GH #600) writes under two. ChessPgn carries the
        // witnessed record; ChessAnalysis carries the calculated derivation DeriveFromParsed
        // deposits in the same Compose call. Only ChessPgn was ever bootstrapped, so the
        // analyzer's source id had no HAS_NAME edge and resolved to nothing: on a live box it
        // showed up as a bare hex id holding 705,141 rows -- the fourth largest source in the
        // substrate, anonymous. A source that writes must be a source that is named, or its
        // volume is invisible to source_counts and every audit that reads it.
        var pgn = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.PgnSourceId, SourceName, ChessVocabulary.PgnTrustClass, ct);
        var analysis = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.AnalysisSourceId, "ChessAnalysis",
            ChessVocabulary.AnalysisTrustClass, ct);
        _canonicalNames = pgn.Concat(analysis).Distinct().ToArray();
    }

    protected override async IAsyncEnumerable<ChessGameRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options, DefaultBatchSize);
        await foreach (var game in StreamNovelGamesAsync(
                           ecosystemPath, _scope, ContainmentReader, ws.Batch, options.ReObservePresent, ct))
            yield return game;
    }

    // ONE pass, ONE pipeline (GH #600): the witnessed record (ChessPgn source) AND the
    // deterministic calculated derivation (positions, move/eval edges, motifs, opening —
    // ChessAnalysis source, via DeriveFromParsed) from the SAME in-memory parse. record.Walk
    // is the tree-sitter parse TryParseGame already produced; the standalone chess-analyze
    // pass used to re-read HAS_MOVETEXT out of Postgres and re-parse it — a full DB round-trip
    // plus a second tree-sitter parse of a game we already hold parsed in hand. SAN replay
    // under chess's fixed rules is deterministic parsing, not a versioned judgment, so it
    // belongs in the recording pass (matches ChessBookDecomposer.ComposeEmbeddedGame).
    // DeriveFromParsed stamps ANALYZED_AT, so the standalone analyzer scan permanently skips
    // games ingested through this fused path; that scan now backfills only games recorded
    // before this fusion landed.
    protected override void Compose(ChessGameRecord record, SubstrateChangeBuilder b)
        => ComposeGame(record, b, _analyzeInline);

    // The fused pass (GH #600), factored out so the fusion contract is directly testable
    // (the class is sealed and Compose is protected). analyzeInline=false reproduces the
    // pre-fusion game-grain-only record.
    internal static void ComposeGame(ChessGameRecord record, SubstrateChangeBuilder b, bool analyzeInline)
    {
        RecordGame(record, b);
        if (analyzeInline) ChessAnalyze.DeriveFromParsed(b, record);
    }

    private static async IAsyncEnumerable<ChessGameRecord> StreamNovelGamesAsync(
        string ecosystemPath, SearchOption scope, ISubstrateReader? reader, int chunkSize,
        bool reObservePresent, [EnumeratorCancellation] CancellationToken ct)
    {
        var chunk = new List<ChessGameRecord>(chunkSize);
        await foreach (var gameText in StreamAllGamesAsync(ecosystemPath, scope, ct))
        {
            if (TryParseGame(gameText) is { } parsed) chunk.Add(parsed);
            if (chunk.Count < chunkSize) continue;
            await foreach (var g in YieldChunkAsync(chunk, reader, reObservePresent, ct)) yield return g;
            chunk.Clear();
        }
        await foreach (var g in YieldChunkAsync(chunk, reader, reObservePresent, ct)) yield return g;
    }

    internal static async IAsyncEnumerable<ChessGameRecord> YieldChunkAsync(
        List<ChessGameRecord> chunk, ISubstrateReader? reader, bool reObservePresent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (reObservePresent || reader is null)
        {
            foreach (var g in chunk) yield return g;
            yield break;
        }
        await foreach (var novel in FilterNovelAsync(chunk, reader, ct)) yield return novel;
    }

    internal static async IAsyncEnumerable<ChessGameRecord> FilterNovelAsync(
        List<ChessGameRecord> chunk, ISubstrateReader? reader, [EnumeratorCancellation] CancellationToken ct)
    {
        if (chunk.Count == 0) yield break;
        if (reader is null) { foreach (var g in chunk) yield return g; yield break; }

        var toProbe = new List<int>(chunk.Count);
        for (int i = 0; i < chunk.Count; i++)
        {
            if (reader.IsProvenPresent(chunk[i].EventId)) continue;
            toProbe.Add(i);
        }

        bool[] present = new bool[chunk.Count];
        if (toProbe.Count > 0)
        {
            var ids = new Hash128[toProbe.Count];
            for (int k = 0; k < toProbe.Count; k++) ids[k] = chunk[toProbe[k]].EventId;
            byte[] bm = await reader.EntitiesExistBitmapAsync(ids, ct).ConfigureAwait(false);
            long bits = (long)bm.Length * 8;
            var proven = new List<Hash128>(toProbe.Count);
            for (int k = 0; k < toProbe.Count; k++)
            {
                if (k >= bits || (bm[k >> 3] & (1 << (k & 7))) == 0) continue;
                present[toProbe[k]] = true;
                proven.Add(ids[k]);
            }
            if (proven.Count > 0) reader.MarkProven(proven);
        }

        for (int i = 0; i < chunk.Count; i++)
            if (!present[i] && !reader.IsProvenPresent(chunk[i].EventId))
                yield return chunk[i];
    }

    internal static async IAsyncEnumerable<string> StreamAllGamesAsync(
        string ecosystemPath, SearchOption scope, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var file in EnumerateFiles(ecosystemPath, scope))
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var gameText in StreamGamesAsync(file, ct).WithCancellation(ct))
                yield return gameText;
        }
    }

    internal static ChessGameRecord? TryParseGame(string gameText)
    {
        var gameBytes = Encoding.UTF8.GetBytes(gameText);
        PgnMovetext.PgnWalkResult walk;
        using (var ast = GrammarDecomposer.Parse(gameBytes, "pgn"))
            walk = PgnMovetext.Walk(ast, gameBytes);
        if (walk.Result is null || walk.Mainline.Count == 0) return null;

        var moves = walk.Mainline.Select(p => p.San).ToList();
        var result = walk.Result.Value;
        var (whiteName, blackName) = ParseNames(gameText);
        string date = PgnGames.TagStr(gameText, "Date");

        // GH #736: line identity is minted HERE, by replay — the content id is the Merkle
        // of the ordered position ids the game passes through, so two sources writing the
        // same play differently ("O-O" vs "0-0", disambiguation variants) collide, and
        // who/when never enters the hash. Replay under chess's fixed rules is deterministic
        // parsing, not a versioned judgment (GH #600), and the novelty gate needs the ids
        // before Compose runs. A game whose SAN does not resolve asserted a line the parser
        // cannot name — dropped at this gate with a counted warning, the same rule the book
        // lane and analyzer already apply.
        string? startFen = PgnGames.TagStr(gameText, "SetUp") == "1"
            ? PgnGames.TagStr(gameText, "FEN") : null;
        var positionIds = TryReplayLine(moves, startFen);
        if (positionIds is null)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"ChessPgnDecomposer: unresolvable SAN, game dropped ({whiteName} vs {blackName} {date})");
            return null;
        }
        var lineId = ChessCompose.LineId(positionIds);

        // The playing-event handle: Seven-Tag-Roster fields closed over the verbatim
        // movetext id — idempotent per record, collision-proof against garbage rosters.
        var movetextId = MovetextId(MovetextSection(gameText));
        if (movetextId is null) return null;
        var eventId = ChessVocabulary.PgnEventId(
            whiteName, blackName, date,
            PgnGames.TagStr(gameText, "Event"), PgnGames.TagStr(gameText, "Round"),
            PgnGames.TagStr(gameText, "Site"), movetextId.Value);

        return new ChessGameRecord(gameText, moves, result, lineId, eventId)
        {
            Walk = walk,
            WhiteName = whiteName,
            BlackName = blackName,
            Date = date,
            PositionIds = positionIds,
            MovetextId = movetextId.Value,
        };
    }

    /// <summary>
    /// Id-only replay of a mainline: the ordered position ids (start position included),
    /// or null when a SAN fails to resolve. Composition is memoized (ChessCompose
    /// PositionMemo), so this shares cost with the fused analyze replay in the same pass.
    /// </summary>
    internal static Hash128[]? TryReplayLine(IReadOnlyList<string> sans, string? startFen)
    {
        var m = new ChessModality();
        // Null start = a start position this parser cannot model. Refuse the line; the caller
        // drops the game with a counted warning rather than replaying it from a board the PGN
        // never asserted.
        if (ChessAnalyze.InitialState(startFen, m) is not { } start) return null;
        var state = start.Initial;
        var ids = new Hash128[sans.Count + 1];
        ids[0] = ChessCompose.Position(m.StateKey(state)).Position.Id;
        var pseudoBuf = new List<ChessMove>(64);
        var legalBuf = new List<ChessMove>(64);
        for (int ply = 0; ply < sans.Count; ply++)
        {
            MoveGen.Legal(state.Board, pseudoBuf, legalBuf);
            var mv = San.Resolve(state.Board, legalBuf, sans[ply]);
            if (mv is null) return null;
            state = m.Apply(state, mv.Value);
            ids[ply + 1] = ChessCompose.Position(m.StateKey(state)).Position.Id;
        }
        return ids;
    }

    // ---- RECORDER: witnessed transcription only. No board replay, no move generation, no
    // geometry, no consensus. Transcribes exactly what the PGN asserts. Everything derived
    // (positions, motifs, opening classification, the Glicko fold) is the analyzer's job
    // (ChessAnalyze). This method stays pure — Compose runs DeriveFromParsed alongside it so
    // the derivation shares this pass's in-memory parse (GH #600); the standalone chess-analyze
    // pass backfills games recorded before that fusion. See docs/specs/08_Record_vs_Calculate_Spec.txt.
    // sourceId defaults to ChessPgn; the chess-book lane records its embedded games under
    // ChessBook so provenance stays with the asserting source (the analyzer scan accepts both).
    //
    // GAME GRAIN ONLY. Per-ply record tokens (SAN/clock/eval/comment/quality on a per-game
    // PlyId subject) are deliberately NOT attested: a PlyId is unique to one game by
    // construction, so every such row is a permanently single-witness consensus cell — dead
    // weight in the Glicko fold (measured ~40M of 62M consensus rows). The verbatim PGN
    // movetext witnessed below (HAS_MOVETEXT, one edge per game) carries every one of those
    // tokens losslessly; readback re-parses it (ChessWitnessHydrator). Aggregating edges
    // (deduped moves/positions carrying outcomes) remain the analyzer's job.
    internal static void RecordGame(ChessGameRecord parsed, SubstrateChangeBuilder b, Hash128? sourceId = null)
    {
        var (gameText, _, result, lineId, eventId) = parsed;
        var src = sourceId ?? ChessVocabulary.PgnSourceId;

        var (whiteElo, blackElo) = ParseElos(gameText);
        // TryParseGame already scanned these header tags; only re-scan for records built elsewhere.
        var (whiteName, blackName) = parsed.WhiteName is { } wn
            ? (wn, parsed.BlackName!)
            : ParseNames(gameText);
        string date = parsed.Date ?? PgnGames.TagStr(gameText, "Date");
        var whitePlayer = EmitPlayer(b, whiteName, src);
        var blackPlayer = EmitPlayer(b, blackName, src);

        EmitGame(b, lineId, eventId, gameText, date, result, whitePlayer, blackPlayer, whiteElo, blackElo, src);
        RecordStartPosition(b, lineId, eventId, gameText, src);
        RecordOpeningHeaders(b, lineId, gameText, src);
        RecordMovetext(b, lineId, eventId, gameText, src);
    }

    private static void RecordStartPosition(
        SubstrateChangeBuilder b, Hash128 lineId, Hash128 eventId, string gameText, Hash128 src)
    {
        if (PgnGames.TagStr(gameText, "SetUp") != "1") return;
        string fen = PgnGames.TagStr(gameText, "FEN");
        if (string.IsNullOrWhiteSpace(fen)) return;
        if (ContentEmitter.Emit(b, fen, src) is { } fid)
            b.AddAttestation(NativeAttestation.Categorical(lineId, "HAS_SETUP", fid, src, eventId, PgnWitnessWeight));
    }

    // Line-grain facts, ctx = null on purpose: each playing that asserts the same
    // ECO/opening for the same line MERGES into one evidence row whose observation
    // count accumulates — every playing is a witness that the line is that opening.
    private static void RecordOpeningHeaders(SubstrateChangeBuilder b, Hash128 lineId, string gameText, Hash128 src)
    {
        string eco = ChessCanonical.Eco(PgnGames.TagStr(gameText, "ECO")) ?? "";
        if (eco.Length > 0) ChessGraph.AppendGameMeta(b, lineId, "GAME_HAS_ECO", eco, PgnWitnessWeight, src);
        string opening = ChessCanonical.OpeningName(PgnGames.TagStr(gameText, "Opening")) ?? "";
        if (opening.Length > 0) ChessGraph.AppendGameMeta(b, lineId, "GAME_HAS_OPENING", opening, PgnWitnessWeight, src);
    }

    // Witness the VERBATIM PGN movetext (clocks, evals, comments, NAGs, result token — the
    // bytes the source asserted) as one content edge on the game.
    //
    // Composed from the movetext's OWN units, not from prose. Handing the raw string to the
    // shared text spine applied UAX #29 sentence segmentation, and PGN's move-number separator
    // is '.', so it split into fragments like "Nd2 Nf6 4. e5 Nfd7 5. " — measured at 82.5%
    // single-use over 3,000 games (81,373 constituents, 67,108 distinct). Content addressing
    // pays off by COLLIDING; prose segmentation of a non-prose format collides almost never,
    // and fills tier-3 content space with fragments that can corroborate with nothing.
    //
    // Ply tokens are shared content: there are only a few thousand distinct SAN tokens in all
    // of chess, so "Nf6" is ONE entity witnessed across millions of games. The movetext is the
    // Merkle over its ordered tokens — the same composition positions use (ChessCompose), with
    // the trajectory carrying the sequence, so the record stays lossless and reconstructible
    // while every unit it is built from is one the corpus actually reuses.
    private static void RecordMovetext(
        SubstrateChangeBuilder b, Hash128 lineId, Hash128 eventId, string gameText, Hash128 src)
    {
        string movetext = MovetextSection(gameText);
        if (movetext.Length == 0) return;

        var tokens = MovetextTokens.Parse(movetext);
        if (tokens.Count == 0) return;

        // Each token through the shared content path: they are words, which is exactly what
        // that path is for, and they dedup across the entire corpus.
        var childIds = new List<Hash128>(tokens.Count);
        foreach (var tok in tokens)
            if (ContentEmitter.Emit(b, tok, src) is { } tid) childIds.Add(tid);
        if (childIds.Count == 0) return;

        var ids = childIds.ToArray();
        var mtId = MovetextId(ids);
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

        b.AddEntity(mtId, EntityTier.Document, ChessVocabulary.MovetextType, src);
        b.AddPhysicality(new PhysicalityRow(
            Id: PhysicalityId.Compute(mtId, PhysicalityType.Content),
            EntityId: mtId,
            SourceId: src,
            Type: PhysicalityType.Content,
            CoordX: 0, CoordY: 0, CoordZ: 0, CoordM: 0,
            HilbertIndex: default,
            TrajectoryXyzm: Trajectory.Build(ids),
            NConstituents: ids.Length,
            AlignmentResidual: null,
            SourceDim: null,
            ObservedAtUnixUs: nowUs));

        // Subject = the LINE, ctx = this playing: two playings with different clock/comment
        // annotations are two movetext documents on one line, distinguished by context.
        b.AddAttestation(NativeAttestation.Categorical(lineId, "HAS_MOVETEXT", mtId, src, eventId, PgnWitnessWeight));
    }

    // Document tier: a movetext is a whole document made of ply tokens.
    private const byte MovetextTier = 4;

    /// <summary>
    /// The composed id of a movetext, from its ordered token ids. ONE definition — the
    /// decomposer writes through it and any caller that needs to name a movetext resolves
    /// through it, so the two can never drift.
    /// </summary>
    internal static Hash128 MovetextId(ReadOnlySpan<Hash128> tokenIds)
        => Hash128.Merkle(MovetextTier, tokenIds);

    /// <summary>The composed id of a movetext surface, tokenized the source's way.</summary>
    internal static Hash128? MovetextId(string movetext)
    {
        var tokens = MovetextTokens.Parse(movetext);
        if (tokens.Count == 0) return null;
        var ids = new List<Hash128>(tokens.Count);
        foreach (var tok in tokens)
            if (ContentEmitter.RootId(tok) is { } tid) ids.Add(tid);
        return ids.Count == 0 ? null : MovetextId(ids.ToArray());
    }

    // The movetext section verbatim: everything after the header-tag block. Header lines start
    // with '['; the first non-blank, non-header line begins the movetext, which then runs to the
    // end of the game text (comment lines inside movetext are included even if they start oddly).
    internal static string MovetextSection(string gameText)
    {
        int i = 0, n = gameText.Length;
        while (i < n)
        {
            int j = gameText.IndexOf('\n', i);
            int end = j < 0 ? n : j;
            var line = gameText.AsSpan(i, end - i).Trim();
            if (line.Length > 0 && line[0] != '[') break;
            i = j < 0 ? n : j + 1;
        }
        return gameText[i..].Trim();
    }

    private static async IAsyncEnumerable<string> StreamGamesAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sb = new StringBuilder(2048);
        var carry = new StringBuilder(256);
        bool inGame = false;
        var buf = new char[1 << 20];
        int read;
        while ((read = await reader.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            int lineStart = 0;
            for (int i = 0; i < read; i++)
            {
                if (buf[i] != '\n') continue;
                int end = i > lineStart && buf[i - 1] == '\r' ? i - 1 : i;
                var tail = buf.AsSpan(lineStart, end - lineStart);
                if (carry.Length > 0)
                {
                    carry.Append(tail);
                    if (carry[^1] == '\r') carry.Length--;
                    ProcessLine(carry.ToString().AsSpan(), sb, ref inGame, out var completed);
                    carry.Clear();
                    if (completed is not null) yield return completed;
                }
                else
                {
                    ProcessLine(tail, sb, ref inGame, out var completed);
                    if (completed is not null) yield return completed;
                }
                lineStart = i + 1;
            }
            if (lineStart < read) carry.Append(buf.AsSpan(lineStart, read - lineStart));
        }
        if (carry.Length > 0)
        {
            var last = carry.ToString().TrimEnd('\r');
            ProcessLine(last.AsSpan(), sb, ref inGame, out var completedLast);
            if (completedLast is not null) yield return completedLast;
        }
        if (sb.Length > 0) yield return sb.ToString();

        static void ProcessLine(ReadOnlySpan<char> line, StringBuilder sb, ref bool inGame, out string? completed)
        {
            completed = null;
            if (line.StartsWith("[Event ", StringComparison.Ordinal))
            {
                if (inGame && sb.Length > 0) { completed = sb.ToString(); sb.Clear(); }
                inGame = true;
            }
            if (inGame) { sb.Append(line); sb.Append('\n'); }
        }
    }

    private const double PgnWitnessWeight = 0.7;

    private static void EmitGame(
        SubstrateChangeBuilder b, Hash128 lineId, Hash128 eventId, string gameText, string date,
        GameOutcome result, Hash128? whitePlayer, Hash128? blackPlayer, int whiteElo, int blackElo,
        Hash128 src)
    {
        // GH #736: two entities. The LINE is content (what was played — shared across every
        // playing); the EVENT is provenance (this playing — who/when/where). Every fact of
        // this playing subjects onto the line with ctx = event, so evidence stays
        // per-playing while consensus cells aggregate across playings.
        b.AddEntity(lineId, EntityTier.Document, ChessVocabulary.GameType, src);
        b.AddEntity(eventId, EntityTier.Document, ChessVocabulary.EventType, src);

        // The ONE record edge whose subject is the event: the event→line join the read
        // side navigates by, carrying this playing's outcome (white POV) in aggregated form.
        b.AddAttestation(NativeAttestation.Aggregated(
            subject: eventId,
            typeId: ChessVocabulary.PlaysLineType,
            obj: lineId,
            sourceId: src,
            contextId: null,
            games: 1,
            sumScoreFp1e9: ChessGraph.ScoreFp1e9(result.ForMover(0)),
            witnessWeight: PgnWitnessWeight));

        // The line's own fold cell: (line, OUTCOME, Chess_Result), one witness per playing —
        // witness_count IS "times played", eff_mu IS how the line fares (white POV).
        ChessGraph.AppendLineOutcome(b, lineId, result.ForMover(0), PgnWitnessWeight, src, eventId);

        if (whitePlayer is { } wp) b.AddAttestation(NativeAttestation.Categorical(lineId, "HAS_WHITE", wp, src, eventId, PgnWitnessWeight));
        if (blackPlayer is { } bp) b.AddAttestation(NativeAttestation.Categorical(lineId, "HAS_BLACK", bp, src, eventId, PgnWitnessWeight));

        // The colour headers above are the RECORD: who sat where, one row per playing.
        // These are the AGGREGATING lane — the same game result carried into the Glicko fold
        // on the player himself, so his record is a consensus cell to be read rather than a
        // 400k-row GROUP BY to be recomputed and cached. Both lanes, always, per the ply law.
        if (whitePlayer is { } w2)
            ChessGraph.AppendPlayerResult(b, w2, blackPlayer, result.ForMover(0), PgnWitnessWeight, src, eventId);
        if (blackPlayer is { } b2)
            ChessGraph.AppendPlayerResult(b, b2, whitePlayer, result.ForMover(1), PgnWitnessWeight, src, eventId);

        Meta(b, lineId, "HAS_EVENT", PgnGames.TagStr(gameText, "Event"), src, eventId);
        Meta(b, lineId, "ON_DATE", date, src, eventId);
        Meta(b, lineId, "HAS_ECO", PgnGames.TagStr(gameText, "ECO"), src, eventId);
        Meta(b, lineId, "HAS_TERMINATION", PgnGames.TagStr(gameText, "Termination"), src, eventId);
        Meta(b, lineId, "HAS_RESULT", result.IsDraw ? "1/2-1/2" : result.Winner == 0 ? "1-0" : "0-1", src, eventId);

        string tc = PgnGames.TagStr(gameText, "TimeControl");
        Meta(b, lineId, "HAS_TIME_CONTROL", tc, src, eventId);
        Meta(b, lineId, "HAS_TC_CLASS", TcClass(tc), src, eventId);

        if (whitePlayer is { } wp2 && whiteElo > 0) Rating(b, wp2, whiteElo, eventId, src);
        if (blackPlayer is { } bp2 && blackElo > 0) Rating(b, bp2, blackElo, eventId, src);
    }

    private static void Meta(
        SubstrateChangeBuilder b, Hash128 line, string rel, string value, Hash128 src, Hash128 eventId)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "?" || value == "-" || value == "????.??.??") return;
        if (ContentEmitter.Emit(b, value, src) is { } vid)
            b.AddAttestation(NativeAttestation.Categorical(line, rel, vid, src, eventId, PgnWitnessWeight));
    }

    private static void Rating(SubstrateChangeBuilder b, Hash128 player, int elo, Hash128 eventId, Hash128 src)
    {
        if (ContentEmitter.Emit(b, elo.ToString(), src) is { } rid)
            b.AddAttestation(NativeAttestation.Categorical(player, "HAS_RATING", rid, src, eventId, PgnWitnessWeight));
    }

    internal static string TcClass(string tc)
    {
        if (string.IsNullOrWhiteSpace(tc) || tc == "-") return "";
        if (tc.Contains('/')) return "classical";
        int plus = tc.IndexOf('+');
        string baseStr = plus >= 0 ? tc[..plus] : tc;
        if (!int.TryParse(baseStr, out int baseSec)) return "";
        return baseSec < 180 ? "bullet" : baseSec < 600 ? "blitz" : baseSec < 1500 ? "rapid" : "classical";
    }

    private static (int White, int Black) ParseElos(string game)
        => (PgnGames.TagInt(game, "WhiteElo"), PgnGames.TagInt(game, "BlackElo"));

    private static (string White, string Black) ParseNames(string game)
        => (PgnGames.TagStr(game, "White"), PgnGames.TagStr(game, "Black"));

    private static Hash128? EmitPlayer(SubstrateChangeBuilder b, string name, Hash128 src)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "?") return null;
        var canonicalId = ChessVocabulary.PlayerId(name);
        ChessVocabulary.EmitPlayer(b, canonicalId, name, src);
        var legacyId = ChessVocabulary.LegacyPlayerId(name);
        if (legacyId != canonicalId)
            b.AddAttestation(NativeAttestation.Categorical(
                canonicalId, "CORRESPONDS_TO", legacyId, src, null, PgnWitnessWeight));
        return canonicalId;
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long games = 0;
        foreach (var f in EnumerateFiles(context.EcosystemPath, _scope))
        {
            try
            {
                games += CountEventHeaderLines(f, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"ChessPgnDecomposer: failed to estimate games in {f}: {ex.Message}");
            }
        }
        return Task.FromResult<long?>(games == 0 ? null : games);
    }

    // Byte-level count of lines starting with "[Event " — same result as ReadLine +
    // StartsWith without a string allocation per line. Line starts follow '\n' or '\r'
    // (an '\r' of a CRLF ends the line; the '\n' then opens a line that can't match '[').
    // A leading UTF-8 BOM is skipped for StreamReader parity.
    private static long CountEventHeaderLines(string path, CancellationToken ct)
    {
        ReadOnlySpan<byte> prefix = "[Event "u8;
        long games = 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: false);
        var buf = new byte[1 << 20];
        int matched = 0;   // prefix bytes matched on the current line; -1 = line can't match
        bool first = true;
        int read;
        while ((read = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            int i = 0;
            if (first)
            {
                first = false;
                if (read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) i = 3;
            }
            for (; i < read; i++)
            {
                byte c = buf[i];
                if (c == (byte)'\n' || c == (byte)'\r') { matched = 0; continue; }
                if (matched < 0) continue;
                if (c == prefix[matched])
                {
                    if (++matched == prefix.Length) { games++; matched = -1; }
                }
                else matched = -1;
            }
        }
        return games;
    }

    // Pre-ingest inventory (GH #492): unit = game, counted as "[Event " headers — the same
    // boundary StreamGamesAsync splits on — so progress denominators match what actually flows.
    public async Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateFiles(context.EcosystemPath, _scope).ToList();
        if (paths.Count == 0) return null;
        if (options.MaxInputUnits > 0)
            return IngestInventory.FromFiles("games", paths, options.MaxInputUnits, ct);

        var files = new List<IngestFileSpec>(paths.Count);
        long total = 0;
        foreach (var p in paths)
        {
            long n = await CountGamesAsync(p, ct).ConfigureAwait(false);
            files.Add(new IngestFileSpec(Path.GetFileName(p), p, n));
            total += n;
        }
        return new IngestInventory("games", total, files);
    }

    private static async Task<long> CountGamesAsync(string path, CancellationToken ct)
    {
        long n = 0;
        using var reader = new StreamReader(path, Encoding.UTF8, true, 1 << 20);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            if (line.StartsWith("[Event ", StringComparison.Ordinal)) n++;
        return n;
    }

    private static IEnumerable<string> EnumerateFiles(string path, SearchOption scope)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        if (File.Exists(path)) { yield return Path.GetFullPath(path); yield break; }
        if (!Directory.Exists(path)) yield break;
        foreach (var f in Directory.EnumerateFiles(path, "*.pgn", scope)
                                   .OrderBy(p => p, StringComparer.Ordinal))
            yield return f;
    }
}

/// <summary>
/// Parsed PGN game (GH #736): <see cref="LineId"/> is the CONTENT identity — the Merkle of
/// the ordered position ids, identical for identical play regardless of who/when —
/// and <see cref="EventId"/> is the PLAYING handle (provenance; the attestation context).
/// The novelty gate keys on the event: re-ingesting the same record skips, while a new
/// playing of an already-known line still records its witnesses.
/// </summary>
public sealed record ChessGameRecord(
    string GameText,
    List<string> Moves,
    GameOutcome Result,
    Hash128 LineId,
    Hash128 EventId)
    : ITrunkRootRecord
{
    internal PgnMovetext.PgnWalkResult Walk { get; init; } = null!;

    // Header tags TryParseGame already scanned, threaded through so RecordGame does not
    // re-scan the full game text. Null when the record was built without a header pass.
    internal string? WhiteName { get; init; }
    internal string? BlackName { get; init; }
    internal string? Date { get; init; }

    // The ordered position ids TryParseGame's identity replay produced (start position
    // included) — LineId is their Merkle; the analyzer's full replay re-derives the same
    // sequence with geometry.
    internal Hash128[] PositionIds { get; init; } = [];
    // The verbatim movetext content id (also folded into EventId).
    internal Hash128 MovetextId { get; init; }

    public Hash128 TrunkRootId => EventId;
}
