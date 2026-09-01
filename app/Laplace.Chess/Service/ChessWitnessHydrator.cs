using System.Runtime.CompilerServices;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Reads the witnessed chess layer from Postgres and streams playings missing a calculated
/// lane's version marker. GH #736 grains: the witnessed record is (event, PLAYS_LINE, line)
/// plus header facts subjected on the LINE with ctx = the EVENT, so the hydrator navigates
/// event → line → context-grouped headers. Two stream grains, matching the two marker
/// grains: per EVENT (analyzer — per-playing testimony) and per LINE (trajectory/stockfish —
/// pure functions of the line). A line's lossless mainline is its ordered trajectory of typed
/// move objects; playings carry only occurrence-specific annotation lanes. SAN and board positions
/// is admitted as stored chess identity.
/// </summary>
internal static class ChessWitnessHydrator
{
    private static readonly Hash128 RelPlaysLine = RelationTypeRegistry.RelationTypeId("PLAYS_LINE");
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
        RelHasWhite.ToBytes(), RelHasBlack.ToBytes(), RelHasSetup.ToBytes(), RelHasResult.ToBytes(),
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

    // Transition replay is testimony-only and safe over historical live/self-play
    // playings too. Analysis deliberately excludes that source because those games
    // already calculate analysis inline; applying that restriction to transitions
    // would leave every pre-transition lab/Lichess game permanently invisible.
    private static byte[][] TransitionWitnessSources() =>
    [
        ChessVocabulary.PgnSourceId.ToBytes(),
        ChessVocabulary.BookSourceId.ToBytes(),
        ChessVocabulary.SourceId.ToBytes(),
    ];

    /// <summary>Recorded playings under witness sources — the analyzer's unit count.</summary>
    /// <remarks>
    /// Name keeps "Events" for call-site stability; the counted type is
    /// <see cref="ChessVocabulary.PlayingType"/> (Copilot #854 / GH #736).
    /// </remarks>
    internal static async Task<long?> CountRecordedEventsAsync(NpgsqlDataSource ds, CancellationToken ct)
        => await NpgsqlSubstrateReads.CountChessEventsWithPlaysLineAsync(
            ds, ChessVocabulary.PlayingType.ToBytes(), RelPlaysLine.ToBytes(),
            WitnessSources(), ct).ConfigureAwait(false);

    internal static async Task<long?> CountTransitionEventsAsync(NpgsqlDataSource ds, CancellationToken ct)
        => await NpgsqlSubstrateReads.CountChessEventsWithPlaysLineAsync(
            ds, ChessVocabulary.PlayingType.ToBytes(), RelPlaysLine.ToBytes(),
            TransitionWitnessSources(), ct).ConfigureAwait(false);

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
        bool includeLive,
        [EnumeratorCancellation] CancellationToken ct)
    {
        chunkSize = Math.Max(1, chunkSize);
        byte[] lastId = Array.Empty<byte>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var playingIds = await FetchRecordedPlayingIdPageAsync(
                    ds, lastId, chunkSize * 4, includeLive, ct)
                .ConfigureAwait(false);
            if (playingIds.Count == 0) yield break;

            lastId = playingIds[^1].ToBytes();

            await foreach (var id in FilterByMarkerAsync(playingIds, reader, chunkSize, markerId, ct))
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
            ds, reader, chunkSize, ev => ChessVocabulary.AnalysisMarkerId(ev, ChessAnalyze.Version),
            includeLive: false, ct))
            yield return g;
    }

    /// <summary>Hydrated per-EVENT stream: one <see cref="ChessWitnessedGame"/> per playing.</summary>
    internal static async IAsyncEnumerable<ChessWitnessedGame> StreamUnanalyzedEventsAsync(
        NpgsqlDataSource ds,
        ISubstrateReader reader,
        int chunkSize,
        Func<Hash128, Hash128> markerId,
        bool includeLive,
        [EnumeratorCancellation] CancellationToken ct)
    {
        chunkSize = Math.Max(1, chunkSize);
        var idChunk = new List<Hash128>(chunkSize);
        await foreach (var eventId in StreamUnanalyzedEventIdsAsync(
                           ds, reader, chunkSize, markerId, includeLive, ct))
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
    /// arbitrary-but-deterministic representative playing supplies headers and result — every
    /// playing of a line reads the same move trajectory by line identity).
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

    /// <summary>
    /// Existing governed players whose game/profile testimony predates the player → name
    /// physicality. Keyset paging reads each missing identity once; no game testimony is
    /// replayed and no name attestation is deposited again.
    /// </summary>
    internal static async IAsyncEnumerable<(Hash128 PlayerId, string Name)>
        StreamPlayersMissingPhysicalityAsync(
            NpgsqlDataSource ds, int chunkSize,
            [EnumeratorCancellation] CancellationToken ct)
    {
        chunkSize = Math.Max(1, chunkSize);
        byte[] after = Array.Empty<byte>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await NpgsqlSubstrateReads.ChessPlayersMissingPhysicalityPageAsync(
                    ds, ChessVocabulary.PlayerType.ToBytes(), (short)PhysicalityType.Content,
                    after, chunkSize, ct)
                .ConfigureAwait(false);
            if (page.Count == 0) yield break;

            after = page[^1].PlayerId;
            foreach (var row in page)
                if (!string.IsNullOrWhiteSpace(row.Name))
                    yield return (Hash128.FromBytes(row.PlayerId), row.Name);
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

    private static async Task<List<Hash128>> FetchRecordedPlayingIdPageAsync(
        NpgsqlDataSource ds, byte[] afterId, int limit, bool includeLive, CancellationToken ct)
    {
        // Chess_PLAYING, not Chess_Event. EmitGame makes the PLAYING the subject of
        // PLAYS_LINE (GH #736: one event holds many playings, so the event cannot carry a
        // per-game outcome). Substrate page helper is still named ChessEventIdPageAsync
        // (generic typed-entity page); the type argument is PlayingType.
        var rows = await NpgsqlSubstrateReads.ChessEventIdPageAsync(
            ds, ChessVocabulary.PlayingType.ToBytes(), RelPlaysLine.ToBytes(),
            includeLive ? TransitionWitnessSources() : WitnessSources(),
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
            if (type == RelHasWhite) gm.White = obj;
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

        // Result is a scalar surface. SetUp and the game mainline are typed trajectories:
        // unpack the batch, reconstruct initial boards, then replay ordered move ids.
        var contentIds = new List<Hash128>();
        void Need(Hash128 id) { if (id != default) contentIds.Add(id); }
        foreach (var (_, _, gm) in wanted)
        {
            Need(gm.ResultObj);
        }

        var setupIds = wanted.Select(static w => w.Meta.SetupObj)
            .Where(static id => id != default).Distinct().ToArray();
        IReadOnlyDictionary<Hash128, Board> setupBoards;
        if (setupIds.Length == 0) setupBoards = new Dictionary<Hash128, Board>();
        else
        {
            var setupBytes = new byte[setupIds.Length][];
            for (int i = 0; i < setupIds.Length; i++) setupBytes[i] = setupIds[i].ToBytes();
            var setupRows = await NpgsqlSubstrateReads.NestedTrajectoryConstituentsAsync(
                ds, setupBytes, ct).ConfigureAwait(false);
            setupBoards = ChessPositionTrajectory.Decode(setupRows);
        }
        var trajectoryOwners = wanted.SelectMany(static w => new[] { w.Line, w.Event })
            .Distinct().ToArray();
        var ownerBytes = new byte[trajectoryOwners.Length][];
        for (int i = 0; i < trajectoryOwners.Length; i++) ownerBytes[i] = trajectoryOwners[i].ToBytes();
        var trajectoryRows = await NpgsqlSubstrateReads.TypedTrajectoryConstituentsAsync(
            ds, ownerBytes,
            [PhysicalityType.Content, PhysicalityType.ChessComment, PhysicalityType.ChessAnnotation],
            ct).ConfigureAwait(false);
        var lanes = new Dictionary<(Hash128 Playing, PhysicalityType Type), List<Hash128>>();
        foreach (var row in trajectoryRows)
        {
            var playing = Hash128.FromBytes(row.ParentId);
            var key = (playing, row.Type);
            if (!lanes.TryGetValue(key, out var values)) lanes[key] = values = [];
            var id = Hash128.FromBytes(row.EntityId);
            values.Add(id);
            if (row.Type != PhysicalityType.Content
                && id != ChessCompose.AnnotationMissing().Id)
                Need(id);
        }
        var textById = await RenderTextBatchAsync(ds, contentIds, ct).ConfigureAwait(false);

        var outList = new List<ChessWitnessedGame>(wanted.Count);
        foreach (var (lineId, eventId, gm) in wanted)
        {
            string? resultStr = gm.ResultObj != default && textById.TryGetValue(gm.ResultObj, out var rs)
                ? rs : null;
            string? startFen = gm.SetupObj != default
                && setupBoards.TryGetValue(gm.SetupObj, out var setupBoard)
                ? setupBoard.ToFen() : null;
            if (!lanes.TryGetValue((lineId, PhysicalityType.Content), out var moveIds)
                || moveIds.Count == 0) continue;
            var replay = ChessReplay.Replay(moveIds, startFen);
            if (replay.Truncated is not null || replay.Plies.Count != moveIds.Count) continue;
            var moves = replay.Plies.Select(static p => p.San).ToArray();
            string?[]? comments = RenderLane(
                lanes, eventId, PhysicalityType.ChessComment, moves.Length, textById);
            string?[]? annotations = RenderLane(
                lanes, eventId, PhysicalityType.ChessAnnotation, moves.Length, textById);
            string annotationPgn = RebuildAnnotatedMovetext(moves, comments);
            string?[]? clockTokens = comments is null
                ? null : PgnClocks.ClockTokens(annotationPgn, moves.Length);
            string?[]? evalTokens = comments is null
                ? null : PgnEvals.EvalTokens(annotationPgn, moves.Length);
            double[]? spent = comments is null
                ? null : PgnClocks.SpentSeconds(annotationPgn, moves.Length);
            string?[]? quality = annotations?.Select(MoveQuality.FromSerializedAnnotations).ToArray();
            if (quality is not null && quality.All(static q => q is null)) quality = null;
            outList.Add(new ChessWitnessedGame(
                lineId, eventId, moves, ParseResult(resultStr),
                gm.White != default ? gm.White : null,
                gm.Black != default ? gm.Black : null,
                startFen, clockTokens, evalTokens, quality, spent) { MoveIds = moveIds });
        }
        return outList;
    }

    private static string?[]? RenderLane(
        IReadOnlyDictionary<(Hash128 Playing, PhysicalityType Type), List<Hash128>> lanes,
        Hash128 playing, PhysicalityType type, int count,
        IReadOnlyDictionary<Hash128, string> textById)
    {
        if (!lanes.TryGetValue((playing, type), out var ids) || ids.Count != count) return null;
        var missing = ChessCompose.AnnotationMissing().Id;
        var values = new string?[count];
        for (int i = 0; i < count; i++)
            if (ids[i] != missing && textById.TryGetValue(ids[i], out var value)) values[i] = value;
        return values;
    }

    private static string RebuildAnnotatedMovetext(IReadOnlyList<string> moves, string?[]? comments)
    {
        var text = new System.Text.StringBuilder(moves.Count * 16);
        for (int i = 0; i < moves.Count; i++)
        {
            if (text.Length > 0) text.Append(' ');
            text.Append(moves[i]);
            if (comments is not null && !string.IsNullOrWhiteSpace(comments[i]))
                text.Append(" { ").Append(comments[i]).Append(" }");
        }
        return text.ToString();
    }

    internal static async Task<ChessWitnessedGame?> TryHydrateAsync(
        NpgsqlDataSource ds, Hash128 eventId, CancellationToken ct)
    {
        var list = await TryHydrateChunkAsync(ds, [eventId], ct).ConfigureAwait(false);
        return list.Count > 0 ? list[0] : null;
    }

    // Per-playing witnessed scaffold: (line, ctx=event) header attestation objects only.
    // The ordered move record is the line's content physicality trajectory.
    private sealed class GameMeta
    {
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

        var texts = await NpgsqlSubstrateReads.RenderTextBatchAsync(ds, bytes, ct)
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
