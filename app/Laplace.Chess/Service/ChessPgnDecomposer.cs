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
    : ComposeDecomposerMultiFile<ChessGameRecord>, IIngestInventoryProvider, IIngestNoOpExplainer
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
            context.Writer, ChessVocabulary.PgnSourceId, SourceName, ChessVocabulary.PgnTrustClass, ct,
            context.Reader);
        var analysis = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.AnalysisSourceId, "ChessAnalysis",
            ChessVocabulary.AnalysisTrustClass, ct, context.Reader);
        _canonicalNames = pgn.Concat(analysis).Distinct().ToArray();

        // Ledger lifecycle moved here from ExtractRecordsAsync: with the file-worker pool there
        // is no longer ONE record stream to bracket. Reset once per run at init, report once at
        // dispose. The ledger itself is already concurrency-safe (ConcurrentDictionary +
        // Interlocked), which is why per-file workers can all drop into it.
        ChessDropLedger.Reset();
    }

    /// <summary>
    /// Reported even on cancellation: a killed run's drop profile is exactly what the operator
    /// needs to decide whether to resume or fix the corpus first.
    /// </summary>
    public override ValueTask DisposeAsync()
    {
        ChessDropLedger.Report(SourceName);
        return base.DisposeAsync();
    }

    // The corpus is many PGN files (Lumbras OTB is 11, 0.07-1.48 GB each) and they carry no
    // cross-file ordering — game identity is content-addressed, so a game in the 1990s file and
    // the same game in the 2000s file collide by hash, not by arrival order. That is exactly the
    // claim the multi-file worker pool already makes for every other multi-file source; chess
    // simply was not on it, and streamed all 11 through one thread (MEASURED: compose is the
    // pipeline's ceiling at ~150 games/s, and the decompose side is a single pinned producer).
    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        bool rootIsFile = File.Exists(ecosystemPath);
        return EnumerateFiles(ecosystemPath, _scope)
            .Select(p =>
            {
                string rel = rootIsFile
                    ? Path.GetFileName(p)
                    : Path.GetRelativePath(ecosystemPath, p).Replace('\\', '/');
                return (p, $"{BatchLabelPrefix}/{rel}");
            })
            .ToArray();
    }

    // ONE file's games, novelty-gated in chunks exactly as before. The gate's proven-set lives on
    // the shared reader (a ConcurrentDictionary, monotone: it only ever gains "present"), so two
    // workers probing the same id race to the same answer. ChessDropLedger is likewise concurrent
    // by construction — its own comment says the parse sites are static and run concurrently.
    protected override async IAsyncEnumerable<ChessGameRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options);
        // --force / ReObservePresent: every game must be fully parsed+composed. Peek+probe
        // before that is pure double tax (measured: 16s kill still on FILE_START).
        if (options.ReObservePresent)
        {
            await foreach (var g in ExtractFileParseDirectAsync(filePath, ws.Batch, ct))
                yield return g;
            yield break;
        }

        // Idempotent path: PlayingId peek+probe, full parse only for novel playings.
        // The same resident-width source plan that sizes full compose also bounds this
        // peek population; chess no longer owns a separate 2,048-game limiter.
        await foreach (var g in ExtractFileSerialPeekAsync(
                           filePath, ws.Batch, reObservePresent: false, ct))
            yield return g;
    }

    /// <summary>Direct full parse — no PlayingId peek (re-observe / force path).</summary>
    private async IAsyncEnumerable<ChessGameRecord> ExtractFileParseDirectAsync(
        string filePath, int batch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var chunk = new List<ChessGameRecord>(batch);
        await foreach (var gameText in StreamFileGamesAsync(filePath, ct))
        {
            if (TryParseGame(gameText) is { } parsed) chunk.Add(parsed);
            if (chunk.Count < batch) continue;
            foreach (var g in chunk) yield return g;
            chunk.Clear();
        }
        foreach (var g in chunk) yield return g;
    }

    private async IAsyncEnumerable<ChessGameRecord> ExtractFileSerialPeekAsync(
        string filePath, int batch, bool reObservePresent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var peeks = new List<ChessPlayingPeek>(batch);
        Task<List<ChessGameRecord>>? pending = null;
        await foreach (var gameText in StreamFileGamesAsync(filePath, ct))
        {
            if (TryPeekPlaying(gameText) is { } peek) peeks.Add(peek);
            if (peeks.Count < batch) continue;
            var handoff = peeks;
            peeks = new List<ChessPlayingPeek>(batch);
            // Start probe BEFORE awaiting the prior — peek of the next batch overlaps the
            // EntitiesExistBitmap round-trip (serial await kept FILE_COMPOSED ~17–21s).
            var next = MaterializeNovelAsync(handoff, ContainmentReader, reObservePresent, ct);
            if (pending is not null)
            {
                foreach (var g in await pending.ConfigureAwait(false))
                    yield return g;
            }
            pending = next;
        }
        if (pending is not null)
        {
            foreach (var g in await pending.ConfigureAwait(false))
                yield return g;
        }
        if (peeks.Count > 0)
        {
            foreach (var g in await MaterializeNovelAsync(
                         peeks, ContainmentReader, reObservePresent, ct).ConfigureAwait(false))
                yield return g;
        }
    }

    private static async Task<List<ChessGameRecord>> MaterializeNovelAsync(
        List<ChessPlayingPeek> peeks, ISubstrateReader? reader, bool reObservePresent,
        CancellationToken ct)
    {
        var list = new List<ChessGameRecord>();
        await foreach (var g in YieldNovelParsedAsync(peeks, reader, reObservePresent, ct)
                           .ConfigureAwait(false))
            list.Add(g);
        // ChessGraph.EmitNodes already trunk-short-circuits on PresenceOracle, but nothing
        // was proving position ids into that oracle — only PlayingIds. MEASURED 2026-08-04:
        // novel OTB year on a DB holding another year staged ~390k entities/WS with ~96%
        // already present at apply verify (~28–50s bitmap). Prove line positions here so
        // compose skips staging the deposited subgraph (entities+phys); attestations still
        // emit and fold. Same EntitiesExistBitmap path Playing novelty already uses.
        await ProbeLinePositionsAsync(list, reader, ct).ConfigureAwait(false);
        return list;
    }

    /// <summary>
    /// Batch-prove <see cref="ChessGameRecord.PositionIds"/> into <paramref name="reader"/>
    /// so <see cref="ChessGraph"/> trunk short-circuit can skip re-staging deposited positions.
    /// </summary>
    internal static async Task ProbeLinePositionsAsync(
        List<ChessGameRecord> games, ISubstrateReader? reader, CancellationToken ct)
    {
        if (reader is null || games.Count == 0) return;
        var unknown = new HashSet<Hash128>();
        for (int g = 0; g < games.Count; g++)
        {
            var positions = games[g].PositionIds;
            for (int i = 0; i < positions.Length; i++)
            {
                var id = positions[i];
                if (!reader.IsProvenPresent(id)) unknown.Add(id);
            }
        }
        if (unknown.Count == 0) return;
        int chunk = IngestSizing.ResolveApplyIo(
            IngestTopology.Current.ApplyPartitions).ProbeChunkIds;
        var ids = new Hash128[unknown.Count];
        unknown.CopyTo(ids);
        for (int i = 0; i < ids.Length; i += chunk)
        {
            int n = Math.Min(chunk, ids.Length - i);
            var slice = new Hash128[n];
            Array.Copy(ids, i, slice, 0, n);
            // Bitmap path MarkProven-s hits; misses stay unproven and compose stages them.
            _ = await reader.EntitiesExistBitmapAsync(slice, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Novelty gate on <see cref="ChessPlayingPeek.PlayingId"/>, then full parse only for novel
    /// games. Present playings are skipped without board replay (lawful idempotent re-ingest).
    /// </summary>
    private static async IAsyncEnumerable<ChessGameRecord> YieldNovelParsedAsync(
        List<ChessPlayingPeek> peeks, ISubstrateReader? reader, bool reObservePresent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (peeks.Count == 0) yield break;
        if (reObservePresent || reader is null)
        {
            foreach (var p in peeks)
                if (TryParseGame(p.GameText) is { } g) yield return g;
            yield break;
        }

        var toProbe = new List<int>(peeks.Count);
        for (int i = 0; i < peeks.Count; i++)
        {
            if (reader.IsProvenPresent(peeks[i].PlayingId)) continue;
            toProbe.Add(i);
        }

        var present = new bool[peeks.Count];
        if (toProbe.Count > 0)
        {
            var ids = new Hash128[toProbe.Count];
            for (int k = 0; k < toProbe.Count; k++) ids[k] = peeks[toProbe[k]].PlayingId;
            byte[] bm = await reader.EntitiesExistBitmapAsync(ids, ct).ConfigureAwait(false);
            var proven = new List<Hash128>(toProbe.Count);
            for (int k = 0; k < toProbe.Count; k++)
            {
                if (!BitmapBits.IsSet(bm, k)) continue;
                present[toProbe[k]] = true;
                proven.Add(ids[k]);
            }
            if (proven.Count > 0) reader.MarkProven(proven);
        }

        for (int i = 0; i < peeks.Count; i++)
        {
            if (present[i] || reader.IsProvenPresent(peeks[i].PlayingId)) continue;
            if (TryParseGame(peeks[i].GameText) is { } g) yield return g;
        }
    }

    /// <summary>
    /// Headers + movetext token id only. Enough to mint <see cref="ChessVocabulary.PgnPlayingId"/>
    /// for the novelty gate — no tree-sitter walk, no board replay.
    /// </summary>
    internal static ChessPlayingPeek? TryPeekPlaying(string gameText)
    {
        if (!TryMovetextTokens(MovetextSection(gameText), out _, out _, out var movetextId))
            return null;
        var (whiteName, blackName) = ParseNames(gameText);
        string date = PgnGames.TagStr(gameText, "Date");
        string eventTag = PgnGames.TagStr(gameText, "Event");
        string siteTag = PgnGames.TagStr(gameText, "Site");
        string roundTag = PgnGames.TagStr(gameText, "Round");
        var playingId = ChessVocabulary.PgnPlayingId(
            whiteName, blackName, date, eventTag, roundTag, siteTag, movetextId);
        return new ChessPlayingPeek(gameText, playingId);
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
            if (reader.IsProvenPresent(chunk[i].PlayingId)) continue;
            toProbe.Add(i);
        }

        bool[] present = new bool[chunk.Count];
        if (toProbe.Count > 0)
        {
            var ids = new Hash128[toProbe.Count];
            for (int k = 0; k < toProbe.Count; k++) ids[k] = chunk[toProbe[k]].PlayingId;
            byte[] bm = await reader.EntitiesExistBitmapAsync(ids, ct).ConfigureAwait(false);
            var proven = new List<Hash128>(toProbe.Count);
            for (int k = 0; k < toProbe.Count; k++)
            {
                if (!BitmapBits.IsSet(bm, k)) continue;
                present[toProbe[k]] = true;
                proven.Add(ids[k]);
            }
            if (proven.Count > 0) reader.MarkProven(proven);
        }

        for (int i = 0; i < chunk.Count; i++)
            if (!present[i] && !reader.IsProvenPresent(chunk[i].PlayingId))
                yield return chunk[i];
    }

    /// <summary>
    /// ONE file's games. This is the unit the multi-file worker pool claims, so it must not
    /// reach outside its own path — the serial directory walk lives in StreamAllGamesAsync,
    /// which is now just this in a loop.
    /// </summary>
    internal static async IAsyncEnumerable<string> StreamFileGamesAsync(
        string file, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // One file can hold several members (a TWIC weekly .zip wraps one .pgn); the
        // enumerator owns each reader's lifetime, so a member must be drained before
        // the next is requested — which is exactly what this loop does.
        foreach (var (_, reader) in ChessInput.OpenMembers(file))
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var gameText in StreamGamesAsync(reader, ct).WithCancellation(ct))
                yield return gameText;
        }
    }

    internal static async IAsyncEnumerable<string> StreamAllGamesAsync(
        string ecosystemPath, SearchOption scope, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var file in EnumerateFiles(ecosystemPath, scope))
        {
            await foreach (var gameText in StreamFileGamesAsync(file, ct).WithCancellation(ct))
                yield return gameText;
        }
    }

    internal static ChessGameRecord? TryParseGame(string gameText)
    {
        var gameBytes = Encoding.UTF8.GetBytes(gameText);
        PgnMovetext.PgnWalkResult walk;
        using (var ast = GrammarDecomposer.Parse(gameBytes, "pgn"))
            walk = PgnMovetext.Walk(ast, gameBytes);
        if (walk.Result is null || walk.Mainline.Count == 0)
        {
            ChessDropLedger.Drop(ChessDropLedger.NoResultOrMoves, Headline(gameText));
            return null;
        }

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
            ChessDropLedger.Drop(
                DropReason(gameText, startFen),
                $"{whiteName} vs {blackName} {date}"
                + (startFen is null ? "" : $" [FEN {startFen}]"));
            return null;
        }
        var lineId = ChessCompose.LineId(positionIds);

        // One tokenize pass — tokens + ids reused by RecordMovetext (no second parse).
        if (!TryMovetextTokens(MovetextSection(gameText), out var mtTokens, out var movetextTokenIds,
                out var movetextId))
        {
            ChessDropLedger.Drop(ChessDropLedger.NoMovetext, $"{whiteName} vs {blackName} {date}");
            return null;
        }
        ChessDropLedger.Kept();
        string eventTag = PgnGames.TagStr(gameText, "Event");
        string siteTag = PgnGames.TagStr(gameText, "Site");
        string roundTag = PgnGames.TagStr(gameText, "Round");
        var eventId = ChessVocabulary.PgnEventId(eventTag, siteTag, date);
        var playingId = ChessVocabulary.PgnPlayingId(
            whiteName, blackName, date, eventTag, roundTag, siteTag, movetextId);

        return new ChessGameRecord(gameText, moves, result, lineId, eventId, playingId)
        {
            Walk = walk,
            WhiteName = whiteName,
            BlackName = blackName,
            Date = date,
            PositionIds = positionIds,
            MovetextId = movetextId,
            MovetextTokenIds = movetextTokenIds,
            MovetextTokens = mtTokens,
        };
    }

    /// <summary>
    /// Which refusal this is. A game that failed to replay from a NON-standard start is
    /// usually a variant, not a corrupt record, and the two want different responses:
    /// "add the variant" versus "the source's data is bad". Chess.com tags every one of
    /// these, so the tag is the evidence; a bare unreadable FEN with no Variant tag stays
    /// <see cref="ChessDropLedger.UnreadableStartPosition"/>.
    /// </summary>
    private static string DropReason(string gameText, string? startFen)
    {
        if (startFen is null) return ChessDropLedger.UnreadableSan;
        string variant = PgnGames.TagStr(gameText, "Variant");
        return string.IsNullOrWhiteSpace(variant) || variant == "?"
            ? ChessDropLedger.UnreadableStartPosition
            : ChessDropLedger.UnmodelledVariant;
    }

    /// <summary>The first header line of a game, for a drop sample that identifies it.</summary>
    private static string Headline(string gameText)
    {
        int nl = gameText.IndexOf('\n');
        var head = nl < 0 ? gameText : gameText[..nl];
        head = head.Trim();
        return head.Length <= 120 ? head : head[..120];
    }

    /// <summary>
    /// Id-only replay of a mainline: the ordered position ids (start position included),
    /// or null when a SAN fails to resolve.
    ///
    /// Uses <see cref="ChessCompose.PositionId(Board, ChessVariantRules?)"/> — never
    /// <see cref="ChessCompose.Position"/> and never <see cref="ChessModality.Apply"/> (Apply
    /// rebuilds the full surface string for repetition history; LineId needs only ids).
    /// Geometry is analyze/ROM.
    /// </summary>
    internal static Hash128[]? TryReplayLine(IReadOnlyList<string> sans, string? startFen)
    {
        var m = new ChessModality();
        // Null start = a start position this parser cannot model. Refuse the line; the caller
        // drops the game with a counted warning rather than replaying it from a board the PGN
        // never asserted.
        if (ChessAnalyze.InitialState(startFen, m) is not { } start) return null;
        var board = start.Initial.Board.Clone();
        var ids = new Hash128[sans.Count + 1];
        ids[0] = ChessCompose.PositionId(board);
        var scratch = new List<ChessMove>(16);
        for (int ply = 0; ply < sans.Count; ply++)
        {
            var mv = San.Resolve(board, sans[ply], scratch);
            if (mv is null) return null;
            Piece moving = board.Squares[mv.Value.From];
            var moveId = ChessCompose.MoveId(moving, mv.Value);
            var tKey = ChessCompose.TransitionKey(ids[ply], moveId);
            MoveApply.Make(board, mv.Value);
            if (ChessTransitionFloor.TryLookup(tKey, out var toId))
            {
                ids[ply + 1] = toId;
            }
            else
            {
                toId = ChessCompose.PositionId(board);
                ids[ply + 1] = toId;
                // Run saturation: next game through this transition is one lookup.
                ChessTransitionFloor.Remember(tKey, toId);
            }
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
    // tokens losslessly; readback re-parses it (ChessWitnessHydrator). The game's HAS_RESULT
    // is the only move/line outcome evidence; queries join the playing to its line trajectory.
    internal static void RecordGame(ChessGameRecord parsed, SubstrateChangeBuilder b, Hash128? sourceId = null)
    {
        var (gameText, _, result, lineId, eventId, playingId) = parsed;
        var src = sourceId ?? ChessVocabulary.PgnSourceId;

        var (whiteElo, blackElo) = ParseElos(gameText);
        // TryParseGame already scanned these header tags; only re-scan for records built elsewhere.
        var (whiteName, blackName) = parsed.WhiteName is { } wn
            ? (wn, parsed.BlackName!)
            : ParseNames(gameText);
        string date = parsed.Date ?? PgnGames.TagStr(gameText, "Date");
        var whitePlayer = EmitPlayer(b, whiteName, src);
        var blackPlayer = EmitPlayer(b, blackName, src);

        EmitGame(b, lineId, eventId, playingId, gameText, date, result, whitePlayer, blackPlayer, whiteElo, blackElo, src);
        RecordStartPosition(b, lineId, playingId, gameText, src);
        RecordOpeningHeaders(b, lineId, gameText, src);
        RecordMovetext(b, lineId, playingId, gameText, src,
            parsed.MovetextTokens, parsed.MovetextTokenIds, parsed.MovetextId);
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
        SubstrateChangeBuilder b, Hash128 lineId, Hash128 eventId, string gameText, Hash128 src,
        IReadOnlyList<string>? movetextTokens = null, Hash128[]? movetextTokenIds = null,
        Hash128? movetextId = null)
    {
        IReadOnlyList<string> tokens;
        Hash128[] ids;
        Hash128 mtId;
        if (movetextTokens is { Count: > 0 } && movetextTokenIds is { Length: > 0 }
            && movetextId is { } known)
        {
            tokens = movetextTokens;
            ids = movetextTokenIds;
            mtId = known;
        }
        else
        {
            string movetext = MovetextSection(gameText);
            if (movetext.Length == 0) return;
            if (!TryMovetextTokens(movetext, out tokens, out ids, out mtId)) return;
        }

        for (int i = 0; i < tokens.Count && i < ids.Length; i++)
            ContentEmitter.Emit(b, tokens[i], src);

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
            ObservedAtUnixUs: 0));

        b.AddAttestation(NativeAttestation.Categorical(lineId, "HAS_MOVETEXT", mtId, src, eventId, PgnWitnessWeight));
    }

    private static bool TryMovetextTokens(
        string movetext, out IReadOnlyList<string> tokens, out Hash128[] ids, out Hash128 mtId)
    {
        tokens = Array.Empty<string>();
        ids = [];
        mtId = default;
        var parsed = MovetextTokens.Parse(movetext);
        if (parsed.Count == 0) return false;
        var list = new List<Hash128>(parsed.Count);
        foreach (var tok in parsed)
            if (ContentEmitter.RootId(tok) is { } tid) list.Add(tid);
        if (list.Count == 0) return false;
        tokens = parsed;
        ids = list.ToArray();
        mtId = MovetextId(ids);
        return true;
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
        TextReader reader, [EnumeratorCancellation] CancellationToken ct)
    {
        var sb = new StringBuilder(2048);
        var carry = new StringBuilder(256);
        bool inGame = false;
        var buf = new char[Math.Max(1,
            IngestSizing.ResolveSequentialIoBufferBytes() / sizeof(char))];
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

    /// <summary>
    /// Surface relation name for both the playing→event edge and the line's event Meta
    /// row. Named once because <c>NativeAttestation.Categorical</c> and <c>Meta</c> both
    /// take the relation as a string, and a second spelled-out "HAS_EVENT" in this file is
    /// a governed-vocabulary literal the ISA g3 ratchet counts — it is shrink-only, so the
    /// second occurrence failed the build on main.
    /// </summary>
    private const string HasEventRelation = "HAS_EVENT";

    private static void EmitGame(
        SubstrateChangeBuilder b, Hash128 lineId, Hash128 eventId, Hash128 playingId,
        string gameText, string date,
        GameOutcome result, Hash128? whitePlayer, Hash128? blackPlayer, int whiteElo, int blackElo,
        Hash128 src)
    {
        // LINE = game content (shared). EVENT = tournament/named event (many games).
        // PLAYING = this PGN game record (novelty + attestation context).
        b.AddEntity(lineId, EntityTier.Document, ChessVocabulary.GameType, src);
        b.AddEntity(eventId, EntityTier.Document, ChessVocabulary.EventType, src);
        b.AddEntity(playingId, EntityTier.Document, ChessVocabulary.PlayingType, src);

        // Playing → line is a structural occurrence/content join. The result is witnessed
        // once through HAS_RESULT below; smuggling the score into this edge records the same
        // observation twice and makes a structural link pretend to be a rating event.
        b.AddAttestation(NativeAttestation.CategoricalResolved(
            playingId, ChessVocabulary.PlaysLineType, lineId, src, null, PgnWitnessWeight));

        // Playing → event (this game belongs to the tournament/named event).
        b.AddAttestation(NativeAttestation.Categorical(
            playingId, HasEventRelation, eventId, src, null, PgnWitnessWeight));

        if (whitePlayer is { } wp) b.AddAttestation(NativeAttestation.Categorical(lineId, "HAS_WHITE", wp, src, playingId, PgnWitnessWeight));
        if (blackPlayer is { } bp) b.AddAttestation(NativeAttestation.Categorical(lineId, "HAS_BLACK", bp, src, playingId, PgnWitnessWeight));

        if (whitePlayer is { } w2)
            ChessGraph.AppendPlayerResult(b, w2, blackPlayer, result.ForMover(0), PgnWitnessWeight, src, playingId);
        if (blackPlayer is { } b2)
            ChessGraph.AppendPlayerResult(b, b2, whitePlayer, result.ForMover(1), PgnWitnessWeight, src, playingId);

        Meta(b, lineId, HasEventRelation, PgnGames.TagStr(gameText, "Event"), src, playingId);
        Meta(b, lineId, "ON_DATE", date, src, playingId);
        Meta(b, lineId, "HAS_ECO", PgnGames.TagStr(gameText, "ECO"), src, playingId);
        Meta(b, lineId, "HAS_TERMINATION", PgnGames.TagStr(gameText, "Termination"), src, playingId);
        Meta(b, lineId, "HAS_RESULT", result.IsDraw ? "1/2-1/2" : result.Winner == 0 ? "1-0" : "0-1", src, playingId);

        string tc = PgnGames.TagStr(gameText, "TimeControl");
        Meta(b, lineId, "HAS_TIME_CONTROL", tc, src, playingId);
        Meta(b, lineId, "HAS_TC_CLASS", TcClass(tc), src, playingId);

        if (whitePlayer is { } wp2 && whiteElo > 0) Rating(b, wp2, whiteElo, playingId, src);
        if (blackPlayer is { } bp2 && blackElo > 0) Rating(b, bp2, blackElo, playingId, src);
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
                games += ChessInput.IsCompressed(f)
                    ? EstimateCompressedGameCount(f)
                    : CountEventHeaderLines(f, ct);
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
            bufferSize: MemoryTopology.CopyStartupBytesPerConnection, useAsync: false);
        var buf = new byte[IngestSizing.ResolveSequentialIoBufferBytes()];
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
    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateFiles(context.EcosystemPath, _scope);
        if (options.MaxInputUnits > 0)
            return Task.FromResult(IngestInventory.FromFiles("games", paths.ToList(), options.MaxInputUnits, ct));

        var files = new List<IngestFileSpec>(paths.Count);
        long total = 0;
        foreach (var p in paths)
        {
            // Sample/exact byte estimate — StreamReader full decode blocked inventory on large PGNs.
            // A compressed member cannot be byte-scanned in place; scale its uncompressed
            // length by the density measured on the plain files, or by the corpus-wide
            // average game size when the whole input is compressed.
            long n = ChessInput.IsCompressed(p)
                ? EstimateCompressedGameCount(p)
                : EtlInventory.EstimatePgnGameCount(p, ct);
            files.Add(new IngestFileSpec(Path.GetFileName(p), p, n));
            total += n;
        }
        return Task.FromResult<IngestInventory?>(new IngestInventory("games", total, files));
    }

    /// <summary>
    /// Games in a compressed member, from its UNCOMPRESSED length over the measured mean
    /// game size of this corpus family (~1.6 KiB across TWIC / Lumbras / chess.com — a
    /// tagged game with clocks runs 1–3 KiB). Inventory is a progress denominator, not a
    /// correctness gate: decompressing a 200 MB archive to count "[Event " before the first
    /// batch would cost more than the ingest it is describing.
    /// </summary>
    private const long MeanCompressedGameBytes = 1_600;

    private static long EstimateCompressedGameCount(string path)
        => Math.Max(1, ChessInput.UncompressedLength(path) / MeanCompressedGameBytes);

    /// <summary>
    /// An empty run is expected when the novelty gate consumed every record it read —
    /// see <see cref="ChessDropLedger.ExplainEmptyRun"/>. Re-ingesting an already-ingested
    /// corpus used to exit 1 with "declares N input unit(s) but ingested 0".
    /// </summary>
    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
        => ChessDropLedger.ExplainEmptyRun(SourceName, declaredInputUnits);

    // Zero matches THROWS (ChessInput.Resolve). It used to yield nothing, which made
    // `ingest chess <wrong-dir>` exit 0 having written not one row — a green that proved
    // nothing, in CI as much as by hand.
    private static IReadOnlyList<string> EnumerateFiles(string path, SearchOption scope)
        => ChessInput.Resolve(path, scope, ChessInput.PgnExtensions, "chess");
}

/// <summary>Cheap novelty handle: PlayingId from headers+movetext, before board replay.</summary>
internal readonly record struct ChessPlayingPeek(string GameText, Hash128 PlayingId);

/// <summary>
/// Parsed PGN game: <see cref="LineId"/> = content (Merkle of position ids);
/// <see cref="EventId"/> = tournament/named event (many games share one);
/// <see cref="PlayingId"/> = this game record (novelty + attestation context).
/// </summary>
public sealed record ChessGameRecord(
    string GameText,
    List<string> Moves,
    GameOutcome Result,
    Hash128 LineId,
    Hash128 EventId,
    Hash128 PlayingId)
    : ITrunkRootRecord
{
    internal PgnMovetext.PgnWalkResult Walk { get; init; } = null!;

    internal string? WhiteName { get; init; }
    internal string? BlackName { get; init; }
    internal string? Date { get; init; }

    internal Hash128[] PositionIds { get; init; } = [];
    internal Hash128 MovetextId { get; init; }
    internal Hash128[] MovetextTokenIds { get; init; } = [];
    internal IReadOnlyList<string> MovetextTokens { get; init; } = Array.Empty<string>();

    public Hash128 TrunkRootId => PlayingId;
}
