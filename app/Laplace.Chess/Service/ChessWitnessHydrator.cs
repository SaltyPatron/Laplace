using System.Runtime.CompilerServices;
using System.Text;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Reads the witnessed chess layer from Postgres and streams playings missing a calculated
/// lane's version marker. GH #736 grains: the witnessed record is (event, PLAYS_LINE, line)
/// plus header facts subjected on the LINE with ctx = the EVENT, so the hydrator navigates
/// event → line → context-grouped headers. Two stream grains, matching the two marker
/// grains: per EVENT (analyzer — per-playing testimony) and per LINE (trajectory/stockfish —
/// pure functions of the line). Per-ply tokens (SAN/clock/eval/quality) are re-parsed from
/// each playing's verbatim HAS_MOVETEXT content, the single lossless per-playing record.
/// </summary>
internal static class ChessWitnessHydrator
{
    private static readonly Hash128 RelPlaysLine = RelationTypeRegistry.RelationTypeId("PLAYS_LINE");
    private static readonly Hash128 RelHasMovetext = RelationTypeRegistry.RelationTypeId("HAS_MOVETEXT");
    private static readonly Hash128 RelHasResult = RelationTypeRegistry.RelationTypeId("HAS_RESULT");
    private static readonly Hash128 RelHasWhite = RelationTypeRegistry.RelationTypeId("HAS_WHITE");
    private static readonly Hash128 RelHasBlack = RelationTypeRegistry.RelationTypeId("HAS_BLACK");
    private static readonly Hash128 RelHasSetup = RelationTypeRegistry.RelationTypeId("HAS_SETUP");

    // The ONLY relation types the hydrate probe consumes off a line entity. Filtering by
    // type in SQL lets the composite index attestations_relation_btree (subject_id, type_id,
    // object_id) drive the probe and stops the wire/CPU cost of pulling every attestation on
    // a line — a line entity also carries tags and other edges the loop below discards.
    private static readonly byte[][] GameRelationTypes =
    [
        RelHasMovetext.ToBytes(), RelHasWhite.ToBytes(), RelHasBlack.ToBytes(),
        RelHasSetup.ToBytes(), RelHasResult.ToBytes(),
    ];

    internal static NpgsqlDataSource? TryResolveDataSource(ISubstrateReader reader) =>
        reader is NpgsqlSubstrateReader npg ? npg.DataSource : null;

    // Witness sources whose recorded playings the analyzer derives. Live/self-play games
    // (ChessSelfPlay source) fold their own outcomes at play time and must NOT be re-derived
    // here — that would double-count them.
    private static byte[][] WitnessSources() =>
    [
        ChessVocabulary.PgnSourceId.ToBytes(),
        ChessVocabulary.BookSourceId.ToBytes(),
    ];

    /// <summary>Recorded playings (events) under witness sources — the analyzer's unit count.</summary>
    internal static async Task<long?> CountRecordedEventsAsync(NpgsqlDataSource ds, CancellationToken ct)
        => await NpgsqlSubstrateReads.CountChessEventsWithPlaysLineAsync(
            ds, ChessVocabulary.EventType.ToBytes(), RelPlaysLine.ToBytes(),
            WitnessSources(), ct).ConfigureAwait(false);

    /// <summary>Distinct recorded lines under witness sources — the line-grain unit count.</summary>
    internal static async Task<long?> CountRecordedLinesAsync(NpgsqlDataSource ds, CancellationToken ct)
        => await NpgsqlSubstrateReads.CountChessLinesWithPlaysLineAsync(
            ds, RelPlaysLine.ToBytes(), WitnessSources(), ct).ConfigureAwait(false);

    // markerId selects the per-EVENT skip marker, so each playing-grain lane (ChessAnalyze)
    // gates its own versioned pass over the same witnessed playings.
    internal static async IAsyncEnumerable<Hash128> StreamUnanalyzedEventIdsAsync(
        NpgsqlDataSource ds,
        ISubstrateReader reader,
        int chunkSize,
        Func<Hash128, Hash128> markerId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        chunkSize = Math.Max(1, chunkSize);
        byte[] lastId = Array.Empty<byte>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var eventIds = await FetchRecordedEventIdPageAsync(ds, lastId, chunkSize * 4, ct)
                .ConfigureAwait(false);
            if (eventIds.Count == 0) yield break;

            lastId = eventIds[^1].ToBytes();

            await foreach (var id in FilterByMarkerAsync(eventIds, reader, chunkSize, markerId, ct))
                yield return id;
        }
    }

    // markerId selects the per-LINE skip marker, so each line-grain lane (trajectory,
    // stockfish) gates its own versioned pass. A line shared by many playings streams ONCE.
    internal static async IAsyncEnumerable<Hash128> StreamUnanalyzedLineIdsAsync(
        NpgsqlDataSource ds,
        ISubstrateReader reader,
        int chunkSize,
        Func<Hash128, Hash128> markerId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        chunkSize = Math.Max(1, chunkSize);
        byte[] lastId = Array.Empty<byte>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var lineIds = await FetchRecordedLineIdPageAsync(ds, lastId, chunkSize * 4, ct)
                .ConfigureAwait(false);
            if (lineIds.Count == 0) yield break;

            lastId = lineIds[^1].ToBytes();

            await foreach (var id in FilterByMarkerAsync(lineIds, reader, chunkSize, markerId, ct))
                yield return id;
        }
    }

    private static async IAsyncEnumerable<Hash128> FilterByMarkerAsync(
        List<Hash128> ids, ISubstrateReader reader, int chunkSize, Func<Hash128, Hash128> markerId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (int off = 0; off < ids.Count; off += chunkSize)
        {
            int take = Math.Min(chunkSize, ids.Count - off);
            var chunk = ids.GetRange(off, take);
            var markers = new Hash128[take];
            for (int i = 0; i < take; i++)
                markers[i] = markerId(chunk[i]);

            byte[] bm = await reader.EntitiesExistBitmapAsync(markers, ct).ConfigureAwait(false);
            for (int i = 0; i < take; i++)
            {
                if (BitmapBits.IsSet(bm, i)) continue;
                yield return chunk[i];
            }
        }
    }

    internal static async IAsyncEnumerable<ChessWitnessedGame> StreamUnanalyzedEventsAsync(
        NpgsqlDataSource ds,
        ISubstrateReader reader,
        int chunkSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var g in StreamUnanalyzedEventsAsync(
            ds, reader, chunkSize, ev => ChessVocabulary.AnalysisMarkerId(ev, ChessAnalyze.Version), ct))
            yield return g;
    }

    /// <summary>Hydrated per-EVENT stream: one <see cref="ChessWitnessedGame"/> per playing.</summary>
    internal static async IAsyncEnumerable<ChessWitnessedGame> StreamUnanalyzedEventsAsync(
        NpgsqlDataSource ds,
        ISubstrateReader reader,
        int chunkSize,
        Func<Hash128, Hash128> markerId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        chunkSize = Math.Max(1, chunkSize);
        var idChunk = new List<Hash128>(chunkSize);
        await foreach (var eventId in StreamUnanalyzedEventIdsAsync(ds, reader, chunkSize, markerId, ct))
        {
            idChunk.Add(eventId);
            if (idChunk.Count < chunkSize) continue;
            foreach (var g in await TryHydrateChunkAsync(ds, idChunk, ct).ConfigureAwait(false))
                yield return g;
            idChunk.Clear();
        }
        if (idChunk.Count > 0)
        {
            foreach (var g in await TryHydrateChunkAsync(ds, idChunk, ct).ConfigureAwait(false))
                yield return g;
        }
    }

    /// <summary>
    /// Hydrated per-LINE stream: ONE <see cref="ChessWitnessedGame"/> per distinct line (an
    /// arbitrary-but-deterministic representative playing supplies the movetext — every
    /// playing of a line replays to the same position sequence by line identity).
    /// </summary>
    internal static async IAsyncEnumerable<ChessWitnessedGame> StreamUnanalyzedLinesAsync(
        NpgsqlDataSource ds,
        ISubstrateReader reader,
        int chunkSize,
        Func<Hash128, Hash128> markerId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        chunkSize = Math.Max(1, chunkSize);
        var idChunk = new List<Hash128>(chunkSize);
        await foreach (var lineId in StreamUnanalyzedLineIdsAsync(ds, reader, chunkSize, markerId, ct))
        {
            idChunk.Add(lineId);
            if (idChunk.Count < chunkSize) continue;
            foreach (var g in await TryHydrateLinesAsync(ds, idChunk, ct).ConfigureAwait(false))
                yield return g;
            idChunk.Clear();
        }
        if (idChunk.Count > 0)
        {
            foreach (var g in await TryHydrateLinesAsync(ds, idChunk, ct).ConfigureAwait(false))
                yield return g;
        }
    }

    internal static async IAsyncEnumerable<Hash128> FilterUnanalyzedEventIdsAsync(
        IReadOnlyList<Hash128> eventIds, ISubstrateReader? reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (eventIds.Count == 0) yield break;
        if (reader is null) { foreach (var id in eventIds) yield return id; yield break; }

        var markers = new Hash128[eventIds.Count];
        for (int i = 0; i < eventIds.Count; i++)
            markers[i] = ChessVocabulary.AnalysisMarkerId(eventIds[i], ChessAnalyze.Version);

        byte[] bm = await reader.EntitiesExistBitmapAsync(markers, ct).ConfigureAwait(false);
        for (int i = 0; i < eventIds.Count; i++)
        {
            if (BitmapBits.IsSet(bm, i)) continue;
            yield return eventIds[i];
        }
    }

    private static async Task<List<Hash128>> FetchRecordedEventIdPageAsync(
        NpgsqlDataSource ds, byte[] afterId, int limit, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.ChessEventIdPageAsync(
            ds, ChessVocabulary.EventType.ToBytes(), RelPlaysLine.ToBytes(), WitnessSources(),
            afterId.Length == 0 ? Array.Empty<byte>() : afterId, limit, ct).ConfigureAwait(false);
        return rows.Select(static b => Hash128.FromBytes(b)).ToList();
    }

    private static async Task<List<Hash128>> FetchRecordedLineIdPageAsync(
        NpgsqlDataSource ds, byte[] afterId, int limit, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.ChessLineIdPageAsync(
            ds, RelPlaysLine.ToBytes(), WitnessSources(),
            afterId.Length == 0 ? Array.Empty<byte>() : afterId, limit, ct).ConfigureAwait(false);
        return rows.Select(static b => Hash128.FromBytes(b)).ToList();
    }

    /// <summary>
    /// Two-hop batched hydrate (GH #736): (1) chunk events → PLAYS_LINE → line ids, (2) one
    /// probe of the lines' header rows, grouped client-side by (line, context) — every
    /// requested playing of every line in the chunk hydrates from ONE scan. Rows belonging
    /// to playings outside the chunk (already-analyzed events sharing a line) are discarded:
    /// re-deriving them here would double-count their testimony.
    /// </summary>
    internal static async Task<IReadOnlyList<ChessWitnessedGame>> TryHydrateChunkAsync(
        NpgsqlDataSource ds, IReadOnlyList<Hash128> eventIds, CancellationToken ct)
    {
        if (eventIds.Count == 0) return Array.Empty<ChessWitnessedGame>();

        // Hop 1: event → line.
        var lineByEvent = new Dictionary<Hash128, Hash128>(eventIds.Count);
        {
            var eventBytes = new byte[eventIds.Count][];
            for (int i = 0; i < eventIds.Count; i++) eventBytes[i] = eventIds[i].ToBytes();
            var edges = await NpgsqlSubstrateReads.AttestationsBySubjectsAndTypeAsync(
                ds, eventBytes, RelPlaysLine.ToBytes(), ct).ConfigureAwait(false);
            foreach (var edge in edges)
                lineByEvent[Hash128.FromBytes(edge.SubjectId)] = Hash128.FromBytes(edge.ObjectId);
        }
        if (lineByEvent.Count == 0) return Array.Empty<ChessWitnessedGame>();

        // Hop 2: the lines' header rows, grouped by (line, event-context).
        var lines = lineByEvent.Values.Distinct().ToArray();
        var groups = await FetchHeaderGroupsAsync(ds, lines, ct).ConfigureAwait(false);

        var wanted = new List<(Hash128 Line, Hash128 Event, GameMeta Meta)>(eventIds.Count);
        foreach (var eventId in eventIds)
        {
            if (!lineByEvent.TryGetValue(eventId, out var lineId)) continue;
            if (!groups.TryGetValue((lineId, eventId), out var gm)) continue;
            wanted.Add((lineId, eventId, gm));
        }
        return await MaterializeAsync(ds, wanted, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Per-LINE hydrate: one representative playing per line (smallest context id, so the
    /// choice is deterministic across runs).
    /// </summary>
    internal static async Task<IReadOnlyList<ChessWitnessedGame>> TryHydrateLinesAsync(
        NpgsqlDataSource ds, IReadOnlyList<Hash128> lineIds, CancellationToken ct)
    {
        if (lineIds.Count == 0) return Array.Empty<ChessWitnessedGame>();

        var groups = await FetchHeaderGroupsAsync(ds, lineIds.ToArray(), ct).ConfigureAwait(false);

        var representative = new Dictionary<Hash128, (Hash128 Event, GameMeta Meta)>(lineIds.Count);
        foreach (var ((lineId, eventId), gm) in groups)
        {
            if (gm.MovetextObj == default) continue;
            if (!representative.TryGetValue(lineId, out var cur) || eventId.CompareToBytewise(cur.Event) < 0)
                representative[lineId] = (eventId, gm);
        }

        var wanted = new List<(Hash128 Line, Hash128 Event, GameMeta Meta)>(lineIds.Count);
        foreach (var lineId in lineIds)
            if (representative.TryGetValue(lineId, out var rep))
                wanted.Add((lineId, rep.Event, rep.Meta));
        return await MaterializeAsync(ds, wanted, ct).ConfigureAwait(false);
    }

    private static async Task<Dictionary<(Hash128 Line, Hash128 Event), GameMeta>> FetchHeaderGroupsAsync(
        NpgsqlDataSource ds, Hash128[] lineIds, CancellationToken ct)
    {
        var lineBytes = new byte[lineIds.Length][];
        for (int i = 0; i < lineIds.Length; i++) lineBytes[i] = lineIds[i].ToBytes();

        var groups = new Dictionary<(Hash128, Hash128), GameMeta>();
        var rows = await NpgsqlSubstrateReads.AttestationsBySubjectsAndTypesAsync(
            ds, lineBytes, GameRelationTypes, ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (row.ContextId is null) continue; // header facts are per-playing; ctx names the event
            var key = (Hash128.FromBytes(row.SubjectId), Hash128.FromBytes(row.ContextId));
            if (!groups.TryGetValue(key, out var gm)) groups[key] = gm = new GameMeta();
            var type = Hash128.FromBytes(row.TypeId);
            var obj = row.ObjectId is null ? default : Hash128.FromBytes(row.ObjectId);
            if (type == RelHasMovetext) gm.MovetextObj = obj;
            else if (type == RelHasWhite) gm.White = obj;
            else if (type == RelHasBlack) gm.Black = obj;
            else if (type == RelHasSetup) gm.SetupObj = obj;
            else if (type == RelHasResult) gm.ResultObj = obj;
        }
        return groups;
    }

    private static async Task<IReadOnlyList<ChessWitnessedGame>> MaterializeAsync(
        NpgsqlDataSource ds, IReadOnlyList<(Hash128 Line, Hash128 Event, GameMeta Meta)> wanted,
        CancellationToken ct)
    {
        if (wanted.Count == 0) return Array.Empty<ChessWitnessedGame>();

        var contentIds = new List<Hash128>();
        void Need(Hash128 id) { if (id != default) contentIds.Add(id); }
        foreach (var (_, _, gm) in wanted)
        {
            Need(gm.MovetextObj);
            Need(gm.ResultObj);
            Need(gm.SetupObj);
        }

        var textById = await RenderTextBatchAsync(ds, contentIds, ct).ConfigureAwait(false);

        var outList = new List<ChessWitnessedGame>(wanted.Count);
        foreach (var (lineId, eventId, gm) in wanted)
        {
            if (gm.MovetextObj == default) continue;
            if (!textById.TryGetValue(gm.MovetextObj, out var movetext)
                || string.IsNullOrWhiteSpace(movetext)) continue;

            // The verbatim movetext IS the per-ply record: moves, clocks, evals, comments and
            // quality annotations are re-parsed from the one witnessed content edge (the
            // lossless law) — no per-ply attestations exist to query.
            var (moves, clockTokens, evalTokens, qualityTokens) = ParseMovetext(movetext);
            if (moves.Length == 0) continue;

            string? resultStr = gm.ResultObj != default && textById.TryGetValue(gm.ResultObj, out var rs)
                ? rs : null;
            string? startFen = gm.SetupObj != default && textById.TryGetValue(gm.SetupObj, out var fen)
                ? fen : null;
            outList.Add(new ChessWitnessedGame(
                lineId, eventId, moves, ParseResult(resultStr),
                gm.White != default ? gm.White : null,
                gm.Black != default ? gm.Black : null,
                startFen, clockTokens, evalTokens, qualityTokens,
                // cutechess dialect (GH #494): spent-time comments survive in the verbatim
                // movetext, so the readback path recovers them exactly like the parse path.
                clockTokens is null ? PgnClocks.SpentSeconds(movetext, moves.Length) : null));
        }
        return outList;
    }

    // Recover the analyzer's witnessed inputs from a game's verbatim movetext. Falls back to a
    // whitespace split for legacy SAN-joined movetext (recorded before the verbatim change) or
    // unparseable content — bare moves, no annotations.
    internal static (string[] Moves, string?[]? ClockTokens, string?[]? EvalTokens, string?[]? QualityTokens)
        ParseMovetext(string movetext)
    {
        PgnMovetext.PgnWalkResult? walk = null;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(movetext);
            using var ast = GrammarDecomposer.Parse(bytes, "pgn");
            walk = PgnMovetext.Walk(ast, bytes);
        }
        catch (Exception)
        {
            // fall through to the legacy split
        }

        if (walk is null || walk.Mainline.Count == 0)
        {
            var legacy = movetext.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return (legacy, null, null, null);
        }

        var moves = new string[walk.Mainline.Count];
        var quality = new string?[moves.Length];
        bool anyQuality = false;
        for (int i = 0; i < moves.Length; i++)
        {
            moves[i] = walk.Mainline[i].San;
            quality[i] = MoveQuality.FromStream(walk.Mainline[i]);
            anyQuality |= quality[i] is not null;
        }
        return (moves,
                PgnClocks.ClockTokens(movetext, moves.Length),
                PgnEvals.EvalTokens(movetext, moves.Length),
                anyQuality ? quality : null);
    }

    internal static async Task<ChessWitnessedGame?> TryHydrateAsync(
        NpgsqlDataSource ds, Hash128 eventId, CancellationToken ct)
    {
        var list = await TryHydrateChunkAsync(ds, [eventId], ct).ConfigureAwait(false);
        return list.Count > 0 ? list[0] : null;
    }

    // Per-playing witnessed scaffold: (line, ctx=event) attestation objects only. Per-ply
    // annotations are NOT read from attestations — they are re-parsed from the verbatim
    // movetext (ParseMovetext).
    private sealed class GameMeta
    {
        public Hash128 MovetextObj;
        public Hash128 White;
        public Hash128 Black;
        public Hash128 SetupObj;
        public Hash128 ResultObj;
    }

    private static async Task<Dictionary<Hash128, string>> RenderTextBatchAsync(
        NpgsqlDataSource ds, IReadOnlyList<Hash128> ids, CancellationToken ct)
    {
        var map = new Dictionary<Hash128, string>();
        if (ids.Count == 0) return map;

        var unique = ids.Distinct().Where(id => id != default).ToArray();
        if (unique.Length == 0) return map;

        var bytes = new byte[unique.Length][];
        for (int i = 0; i < unique.Length; i++) bytes[i] = unique[i].ToBytes();

        var texts = await NpgsqlSubstrateReads.RenderTextBatchAsync(ds, bytes, 48, ct)
            .ConfigureAwait(false);
        if (texts is null || texts.Length != unique.Length) return map;
        for (int i = 0; i < unique.Length; i++)
        {
            if (!string.IsNullOrEmpty(texts[i])) map[unique[i]] = texts[i];
        }
        return map;
    }

    private static GameOutcome ParseResult(string? s) => s switch
    {
        "1-0" => new GameOutcome(0),
        "0-1" => new GameOutcome(1),
        "1/2-1/2" => new GameOutcome(null),
        _ => new GameOutcome(null),
    };
}
