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
/// pure functions of the line). A line's lossless Content manifest is the exact Merkle preimage
/// [start-position, ordered typed moves]; playings carry only occurrence-specific annotation
/// lanes. SAN and the subsequent board walk are deterministic realizations of that stored identity.
/// </summary>
internal static class ChessWitnessHydrator
{
    private static readonly Hash128 RelPlaysLine = RelationTypeRegistry.RelationTypeId("PLAYS_LINE");
    private static readonly Hash128 RelHasResult = RelationTypeRegistry.RelationTypeId("HAS_RESULT");
    private static readonly Hash128 RelHasWhite = RelationTypeRegistry.RelationTypeId("HAS_WHITE");
    private static readonly Hash128 RelHasBlack = RelationTypeRegistry.RelationTypeId("HAS_BLACK");
    private static readonly Hash128 RelHasSetup = RelationTypeRegistry.RelationTypeId("HAS_SETUP");

    private static readonly byte[][] GameRelationTypes =
    [
        RelHasWhite.ToBytes(), RelHasBlack.ToBytes(), RelHasSetup.ToBytes(), RelHasResult.ToBytes(),
    ];

    internal static NpgsqlDataSource? TryResolveDataSource(ISubstrateReader reader) =>
        reader is NpgsqlSubstrateReader npg ? npg.DataSource : null;

    private static byte[][] WitnessSources() =>
    [
        ChessVocabulary.PgnSourceId.ToBytes(),
        ChessVocabulary.BookSourceId.ToBytes(),
    ];

    private static byte[][] TransitionWitnessSources() =>
    [
        ChessVocabulary.PgnSourceId.ToBytes(),
        ChessVocabulary.BookSourceId.ToBytes(),
        ChessVocabulary.SourceId.ToBytes(),
    ];

    internal static async Task<long?> CountRecordedEventsAsync(NpgsqlDataSource ds, CancellationToken ct)
        => await NpgsqlSubstrateReads.CountChessEventsWithPlaysLineAsync(
            ds, ChessVocabulary.PlayingType.ToBytes(), RelPlaysLine.ToBytes(),
            WitnessSources(), ct).ConfigureAwait(false);

    internal static async Task<long?> CountTransitionEventsAsync(NpgsqlDataSource ds, CancellationToken ct)
        => await NpgsqlSubstrateReads.CountChessEventsWithPlaysLineAsync(
            ds, ChessVocabulary.PlayingType.ToBytes(), RelPlaysLine.ToBytes(),
            TransitionWitnessSources(), ct).ConfigureAwait(false);

    internal static async Task<long?> CountRecordedLinesAsync(NpgsqlDataSource ds, CancellationToken ct)
        => await NpgsqlSubstrateReads.CountChessLinesWithPlaysLineAsync(
            ds, RelPlaysLine.ToBytes(), WitnessSources(), ct).ConfigureAwait(false);

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

    internal static async IAsyncEnumerable<ChessWitnessedGame> StreamUnanalyzedEventsAsync(
        NpgsqlDataSource ds,
        ISubstrateReader reader,
        int chunkSize,
        Func<Hash128, Hash128> markerId,
        bool includeLive,
        [EnumeratorCancellation] CancellationToken ct,
        long? strictMaterializationBytes = null)
    {
        chunkSize = Math.Max(1, chunkSize);
        var idChunk = new List<Hash128>(chunkSize);
        await foreach (var eventId in StreamUnanalyzedEventIdsAsync(
                           ds, reader, chunkSize, markerId, includeLive, ct))
        {
            idChunk.Add(eventId);
            if (idChunk.Count < chunkSize) continue;
            var hydrated = strictMaterializationBytes is { } maximumBytes
                ? await HydratePositionOutcomeInputsAsync(ds, idChunk, maximumBytes, ct).ConfigureAwait(false)
                : await TryHydrateChunkAsync(ds, idChunk, ct).ConfigureAwait(false);
            foreach (var g in hydrated)
                yield return g;
            idChunk.Clear();
        }
        if (idChunk.Count > 0)
        {
            var hydrated = strictMaterializationBytes is { } maximumBytes
                ? await HydratePositionOutcomeInputsAsync(ds, idChunk, maximumBytes, ct).ConfigureAwait(false)
                : await TryHydrateChunkAsync(ds, idChunk, ct).ConfigureAwait(false);
            foreach (var g in hydrated)
                yield return g;
        }
    }

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
                    ds, ChessVocabulary.PlayerType.ToBytes(), (short)PhysicalityType.Projection,
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

    internal static async Task<List<Hash128>> FetchRecordedPlayingIdPageAsync(
        NpgsqlDataSource ds, byte[] afterId, int limit, bool includeLive, CancellationToken ct)
    {
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

    internal static async Task<IReadOnlyList<ChessWitnessedGame>> TryHydrateChunkAsync(
        NpgsqlDataSource ds, IReadOnlyList<Hash128> eventIds, CancellationToken ct)
    {
        if (eventIds.Count == 0) return Array.Empty<ChessWitnessedGame>();

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
    /// Strict inputs for the position-outcome version transition: the complete line,
    /// its start position, and the literal recorded result for every selected playing.
    /// Annotation, clock and engine-evaluation lanes do not participate in this recipe.
    /// Unknown/missing/conflicting recorded inputs refuse before source eviction.
    /// </summary>
    internal static async Task<IReadOnlyList<ChessWitnessedGame>> HydratePositionOutcomeInputsAsync(
        NpgsqlDataSource ds, IReadOnlyList<Hash128> playingIds, long maximumMaterializedBytes,
        CancellationToken ct)
    {
        if (playingIds.Count == 0) return [];
        var budget = new NpgsqlSubstrateReads.ChessWitnessReadBudget(maximumMaterializedBytes);
        budget.Reserve(checked(65536L + playingIds.Count * 512L));
        byte[][] selected = playingIds.Select(id => id.ToBytes()).ToArray();
        var bindings = await NpgsqlSubstrateReads.ChessWitnessInputsAsync(ds, selected,
            [RelPlaysLine.ToBytes()], TransitionWitnessSources(), null, budget, ct).ConfigureAwait(false);
        var lineByPlaying = new Dictionary<Hash128, Hash128>();
        foreach (var row in bindings)
        {
            if (row.Object is null || row.Context is not null)
                throw new InvalidDataException("Recorded playing has an invalid PLAYS_LINE binding.");
            Hash128 playing = Hash128.FromBytes(row.Subject), line = Hash128.FromBytes(row.Object);
            if (lineByPlaying.TryGetValue(playing, out var previous) && previous != line)
                throw new InvalidDataException("Recorded playing has competing PLAYS_LINE bindings.");
            lineByPlaying[playing] = line;
        }
        if (playingIds.Any(id => !lineByPlaying.ContainsKey(id)))
            throw new InvalidDataException("Selected playing corpus could not be completely reconstructed: missing recorded line binding.");

        var lines = lineByPlaying.Values.Distinct().ToArray();
        var headers = await NpgsqlSubstrateReads.ChessWitnessInputsAsync(ds,
            lines.Select(id => id.ToBytes()).ToArray(), GameRelationTypes,
            TransitionWitnessSources(), selected, budget, ct).ConfigureAwait(false);
        var values = new Dictionary<(Hash128 Playing, Hash128 Type), Hash128>();
        foreach (var row in headers)
        {
            if (row.Context is null || row.Object is null)
                throw new InvalidDataException("Recorded playing has an incomplete header binding.");
            Hash128 playing = Hash128.FromBytes(row.Context), line = Hash128.FromBytes(row.Subject);
            if (lineByPlaying[playing] != line)
                throw new InvalidDataException("Recorded playing header names a competing line.");
            var key = (playing, Hash128.FromBytes(row.Type));
            var value = Hash128.FromBytes(row.Object);
            if (values.TryGetValue(key, out var previous) && previous != value)
                throw new InvalidDataException("Recorded playing has competing header values.");
            values[key] = value;
        }
        var wanted = new List<(Hash128 Line, Hash128 Event, GameMeta Meta)>(playingIds.Count);
        foreach (var playing in playingIds)
        {
            if (!values.TryGetValue((playing, RelHasResult), out var result))
                throw new InvalidDataException("Selected playing corpus could not be completely reconstructed: missing recorded result.");
            wanted.Add((lineByPlaying[playing], playing, new GameMeta
            {
                ResultObj = result,
                White = values.GetValueOrDefault((playing, RelHasWhite)),
                Black = values.GetValueOrDefault((playing, RelHasBlack)),
                SetupObj = values.GetValueOrDefault((playing, RelHasSetup)),
            }));
        }

        var resultIds = wanted.Select(w => w.Meta.ResultObj).Distinct().ToArray();
        var resultText = await ReadStrictResultsAsync(ds, resultIds, budget, ct).ConfigureAwait(false);
        // Reserve actual encoded geometry and native-expanded work before the common
        // hydrator requests expanded constituents or constructs replay/board arrays.
        var lineShape = await NpgsqlSubstrateReads.ChessContentShapeAsync(ds, lines.Select(id => id.ToBytes()).ToArray(),
            budget, 4096, ct).ConfigureAwait(false);
        var lineMultiplicity = wanted.GroupBy(row => row.Line).ToDictionary(group => group.Key, group => group.Count());
        foreach (var row in lineShape)
            budget.Reserve(checked((long)row.RunLength * (lineMultiplicity[Hash128.FromBytes(row.Parent)] - 1) * 4096));
        var setupIds = wanted.Select(w => w.Meta.SetupObj).Where(id => id != default).Distinct().ToArray();
        var setupShape = await NpgsqlSubstrateReads.ChessContentShapeAsync(ds,
            setupIds.Select(id => id.ToBytes()).ToArray(), budget, 512, ct).ConfigureAwait(false);
        var setupAtoms = setupShape.Select(row => Hash128.FromBytes(row.Child)).Distinct().ToArray();
        var atomShape = await NpgsqlSubstrateReads.ChessContentShapeAsync(ds,
            setupAtoms.Select(id => id.ToBytes()).ToArray(), budget, 512, ct).ConfigureAwait(false);
        var atomFields = atomShape.GroupBy(row => Hash128.FromBytes(row.Parent))
            .ToDictionary(group => group.Key, group => group.Sum(row => (long)row.RunLength));
        foreach (var row in setupShape)
            budget.Reserve(checked((long)row.RunLength * atomFields.GetValueOrDefault(Hash128.FromBytes(row.Child)) * 512));
        var games = await MaterializeAsync(ds, wanted, ct, resultText).ConfigureAwait(false);
        if (games.Count != playingIds.Count || games.Select(game => game.PlayingId).Distinct().Count() != playingIds.Count)
            throw new InvalidDataException("Selected playing corpus could not be completely reconstructed from its recorded inputs.");
        return games;
    }

    private static async Task<IReadOnlyDictionary<Hash128, string>> ReadStrictResultsAsync(
        NpgsqlDataSource ds, IReadOnlyList<Hash128> selected,
        NpgsqlSubstrateReads.ChessWitnessReadBudget budget, CancellationToken ct)
    {
        // The finite result vocabulary uses the ordinary native text composer. Its
        // actual emitted manifests delimit the graph the renderer may visit; this
        // avoids admitting an arbitrary text closure under a purported result id.
        var supported = new Dictionary<Hash128, string>();
        var expected = new Dictionary<Hash128, Hash128[]>();
        foreach (string token in new[] { "1-0", "0-1", "1/2-1/2" })
        {
            using var tree = ContentTierSpine.BuildTree(System.Text.Encoding.UTF8.GetBytes(token))
                ?? throw new InvalidDataException("Cannot compose the canonical recorded result vocabulary.");
            var ids = tree.NodeIds();
            supported[ids[tree.NaturalUnitIndex()]] = token;
            for (uint index = 0; index < tree.NodeCount; index++)
            {
                if (!tree.ShouldEmitCompositional(index)) continue;
                var node = tree.GetNode(index);
                var children = new Hash128[node.ChildCount];
                for (uint child = 0; child < node.ChildCount; child++)
                    children[child] = ids[node.FirstChildIdx + child];
                expected[ids[index]] = children;
            }
        }
        if (selected.Any(id => !supported.ContainsKey(id)))
            throw new InvalidDataException("Recorded result is unsupported or unknown; '*' is not a draw.");
        var actual = await NpgsqlSubstrateReads.ChessContentShapeAsync(ds,
            expected.Keys.Select(id => id.ToBytes()).ToArray(), budget, 256, ct).ConfigureAwait(false);
        var byParent = actual.GroupBy(row => Hash128.FromBytes(row.Parent))
            .ToDictionary(group => group.Key, group => group.ToArray());
        // Only required result graphs must be present. The complete finite graph is
        // checked when present so native rendering cannot follow unbounded extras.
        foreach (var (parent, rows) in byParent)
        {
            var children = expected[parent];
            int offset = 0;
            foreach (var row in rows)
            {
                if (row.RunLength > children.Length - offset)
                    throw new InvalidDataException("Recorded result Content disagrees with its canonical native manifest.");
                for (int repeat = 0; repeat < row.RunLength; repeat++)
                    if (children[offset++] != Hash128.FromBytes(row.Child))
                        throw new InvalidDataException("Recorded result Content disagrees with its canonical native manifest.");
            }
            if (offset != children.Length)
                throw new InvalidDataException("Recorded result Content is incomplete.");
        }
        budget.Reserve(4096);
        var rendered = await NpgsqlSubstrateReads.RenderPreflightedChessResultsAsync(ds,
            selected.Select(id => id.ToBytes()).ToArray(), ContentTierSpine.MaxContentTier + 1, ct).ConfigureAwait(false);
        if (rendered is null || rendered.Length != selected.Count)
            throw new InvalidDataException("Recorded result could not be rendered completely.");
        var result = new Dictionary<Hash128, string>();
        for (int index = 0; index < selected.Count; index++)
        {
            if (rendered[index] != supported[selected[index]])
                throw new InvalidDataException("Recorded result is missing or could not be rendered exactly.");
            result[selected[index]] = rendered[index];
        }
        return result;
    }

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
            if (row.ContextId is null) continue;
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
        CancellationToken ct, IReadOnlyDictionary<Hash128, string>? positionOutcomeResults = null)
    {
        if (wanted.Count == 0) return Array.Empty<ChessWitnessedGame>();

        var contentIds = new List<Hash128>();
        void Need(Hash128 id) { if (id != default) contentIds.Add(id); }
        if (positionOutcomeResults is null)
            foreach (var (_, _, gm) in wanted) Need(gm.ResultObj);

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
        var trajectoryOwners = wanted.SelectMany(w => positionOutcomeResults is null ? new[] { w.Line, w.Event } : new[] { w.Line })
            .Distinct().ToArray();
        var ownerBytes = new byte[trajectoryOwners.Length][];
        for (int i = 0; i < trajectoryOwners.Length; i++) ownerBytes[i] = trajectoryOwners[i].ToBytes();
        var trajectoryRows = await NpgsqlSubstrateReads.TypedTrajectoryConstituentsAsync(
            ds, ownerBytes,
            positionOutcomeResults is null
                ? [PhysicalityType.Content, PhysicalityType.ChessComment, PhysicalityType.ChessAnnotation]
                : [PhysicalityType.Content],
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
        var textById = positionOutcomeResults is null
            ? await RenderTextBatchAsync(ds, contentIds, ct).ConfigureAwait(false)
            : positionOutcomeResults;

        var outList = new List<ChessWitnessedGame>(wanted.Count);
        foreach (var (lineId, eventId, gm) in wanted)
        {
            string? resultStr = gm.ResultObj != default && textById.TryGetValue(gm.ResultObj, out var rs)
                ? rs : null;
            string? startFen = gm.SetupObj != default
                && setupBoards.TryGetValue(gm.SetupObj, out var setupBoard)
                ? setupBoard.ToFen() : null;
            if (positionOutcomeResults is not null && gm.SetupObj != default && startFen is null)
                throw new InvalidDataException("Selected playing corpus could not be completely reconstructed: missing recorded start position.");

            var modality = new ChessModality();
            if (ChessAnalyze.InitialState(startFen, modality) is not { } initial) continue;
            Hash128 expectedStart = ChessCompose.PositionId(initial.Initial.Board);
            Hash128 startPositionId;
            Hash128[] moveIds;
            if (lineId == expectedStart)
            {
                // A zero-move playing reuses its sole start constituent under the native
                // singleton content law. Its board Content belongs to the position: do
                // not reinterpret those children as moves or emit a self-reference.
                startPositionId = expectedStart;
                moveIds = [];
            }
            else
            {
                if (!lanes.TryGetValue((lineId, PhysicalityType.Content), out var contentManifest)
                    || contentManifest.Count == 0) continue;
                startPositionId = contentManifest[0];
                moveIds = contentManifest.Skip(1).ToArray();
            }
            if (startPositionId != expectedStart) continue;
            if (ChessCompose.LineId(startPositionId, moveIds) != lineId) continue;

            var replay = ChessReplay.Replay(moveIds, startFen);
            if (replay.Truncated is not null || replay.Plies.Count != moveIds.Length) continue;
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
                startFen, clockTokens, evalTokens, quality, spent)
                { MoveIds = moveIds, StartPositionId = startPositionId });
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
