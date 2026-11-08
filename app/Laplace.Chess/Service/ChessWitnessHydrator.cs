using System.Runtime.CompilerServices;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Reads recorded playings and streams those missing a calculation witness's version marker.
/// A playing is (event, PLAYS_LINE, line) plus header attestations on the line with the event
/// as context, so hydration walks event → line → context-grouped headers. Two stream grains
/// match the two marker grains: per event (per-playing testimony) and per line (functions of
/// the line alone). A line's Content manifest is its exact Merkle preimage [start position,
/// ordered typed moves]; playings carry only occurrence-specific annotations. SAN and the board
/// walk are deterministic realizations of that identity.
/// </summary>
internal static partial class ChessWitnessHydrator
{
    private static readonly Hash128 RelPlaysLine = RelationTypeRegistry.RelationTypeId("PLAYS_LINE");
    private static readonly Hash128 RelHasResult = ChessVocabulary.HasResultType;
    // A line's players are HAS_PLAYER or direct PGN White/Black.
    private static readonly Hash128 RelHasPlayer = ChessVocabulary.RelTypeHasPlayer;
    private static readonly Hash128 RelWhite = ChessVocabulary.WhiteType;
    private static readonly Hash128 RelBlack = ChessVocabulary.BlackType;

    private enum Side : byte { None, White, Black }

    private static Side SideOf(Hash128 type, byte[]? qualifiers)
    {
        if (type == RelWhite) return Side.White;
        if (type == RelBlack) return Side.Black;
        if (type != RelHasPlayer || qualifiers is not { Length: 32 }) return Side.None;
        Mask256 mask = Mask256.FromByteArray(qualifiers);
        return !(mask & ChessVocabulary.WhiteSide).IsZero ? Side.White
             : !(mask & ChessVocabulary.BlackSide).IsZero ? Side.Black
             : Side.None;
    }
    private static readonly Hash128 RelHasSetup = ChessVocabulary.HasSetupType;

    private static readonly byte[][] GameRelationTypes =
    [
        RelHasPlayer.ToBytes(), RelWhite.ToBytes(), RelBlack.ToBytes(),
        RelHasSetup.ToBytes(), RelHasResult.ToBytes(),
    ];

    internal static NpgsqlDataSource? TryResolveDataSource(ISubstrateReader reader) =>
        reader is NpgsqlSubstrateReader npg ? npg.DataSource : null;

    private static byte[][] WitnessSources() =>
    [
        ChessVocabulary.PgnSourceId.ToBytes(),
        ChessVocabulary.BookSourceId.ToBytes(),
        ..ChessCorpus.Names.Select(static name => ChessCorpus.SourceId(name).ToBytes()),
    ];

    private static byte[][] TransitionWitnessSources() =>
    [
        ChessVocabulary.PgnSourceId.ToBytes(),
        ChessVocabulary.BookSourceId.ToBytes(),
        ChessVocabulary.SourceId.ToBytes(),
        ..ChessCorpus.Names.Select(static name => ChessCorpus.SourceId(name).ToBytes()),
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
        int completionLayer,
        IReadOnlyList<Hash128> completionOwners,
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

            await foreach (var id in FilterByMarkerAsync(playingIds, reader, chunkSize, markerId, completionLayer, completionOwners, ct))
                yield return id;
        }
    }

    internal static async IAsyncEnumerable<Hash128> StreamUnanalyzedLineIdsAsync(
        NpgsqlDataSource ds,
        ISubstrateReader reader,
        int chunkSize,
        Func<Hash128, Hash128> markerId,
        int completionLayer,
        IReadOnlyList<Hash128> completionOwners,
        [EnumeratorCancellation] CancellationToken ct,
        Hash128? completionContextId = null)
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

            await foreach (var id in FilterByMarkerAsync(lineIds, reader, chunkSize, markerId, completionLayer, completionOwners, ct, completionContextId))
                yield return id;
        }
    }

    internal static async IAsyncEnumerable<Hash128> FilterByMarkerAsync(
        IReadOnlyList<Hash128> ids, ISubstrateReader reader, int chunkSize,
        Func<Hash128, Hash128> markerId, int completionLayer,
        IReadOnlyList<Hash128> completionOwners,
        [EnumeratorCancellation] CancellationToken ct,
        Hash128? completionContextId = null)
    {
        if (completionOwners.Count == 0)
            throw new ArgumentException("unit completion requires an explicit output owner", nameof(completionOwners));
        chunkSize = Math.Max(1, chunkSize);
        for (int off = 0; off < ids.Count; off += chunkSize)
        {
            int take = Math.Min(chunkSize, ids.Count - off);
            var receipts = new IngestUnitCompletionKey[checked(take * completionOwners.Count)];
            for (int i = 0; i < take; i++)
                for (int owner = 0; owner < completionOwners.Count; owner++)
                    receipts[i * completionOwners.Count + owner] = IngestUnitCompletion.Key(
                        markerId(ids[off + i]), completionOwners[owner], completionLayer, completionContextId);

            var present = await reader.CompletedUnitsAsync(receipts, ct).ConfigureAwait(false);
            for (int i = 0; i < take; i++)
            {
                bool complete = false;
                for (int owner = 0; owner < completionOwners.Count; owner++)
                    complete |= present.Contains(receipts[i * completionOwners.Count + owner]);
                if (!complete) yield return ids[off + i];
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
            includeLive: false, 21, [ChessAnalyze.SourceId], ct))
            yield return g;
    }

    internal static async IAsyncEnumerable<ChessWitnessedGame> StreamUnanalyzedEventsAsync(
        NpgsqlDataSource ds,
        ISubstrateReader reader,
        int chunkSize,
        Func<Hash128, Hash128> markerId,
        bool includeLive,
        int completionLayer,
        IReadOnlyList<Hash128> completionOwners,
        [EnumeratorCancellation] CancellationToken ct,
        long? strictMaterializationBytes = null)
    {
        chunkSize = Math.Max(1, chunkSize);
        var idChunk = new List<Hash128>(chunkSize);
        await foreach (var eventId in StreamUnanalyzedEventIdsAsync(
                           ds, reader, chunkSize, markerId, includeLive, completionLayer, completionOwners, ct))
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
        int completionLayer,
        IReadOnlyList<Hash128> completionOwners,
        [EnumeratorCancellation] CancellationToken ct,
        Hash128? completionContextId = null)
    {
        chunkSize = Math.Max(1, chunkSize);
        var idChunk = new List<Hash128>(chunkSize);
        await foreach (var lineId in StreamUnanalyzedLineIdsAsync(ds, reader, chunkSize, markerId, completionLayer, completionOwners, ct, completionContextId))
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

        await foreach (var id in FilterByMarkerAsync(
                           eventIds, reader, Math.Max(1, eventIds.Count),
                           id => ChessVocabulary.AnalysisMarkerId(id, ChessAnalyze.Version),
                           21, [ChessAnalyze.SourceId], ct))
            yield return id;
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
}
