using System.Runtime.CompilerServices;
using global::Npgsql;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Reads the witnessed chess layer from Postgres and streams games missing the current
/// analysis-version marker. Batch hydration + render_text_batch — no PGN files.
/// </summary>
internal static class ChessWitnessHydrator
{
    private static readonly Hash128 RelHasMovetext = RelationTypeRegistry.RelationTypeId("HAS_MOVETEXT");
    private static readonly Hash128 RelHasResult = RelationTypeRegistry.RelationTypeId("HAS_RESULT");
    private static readonly Hash128 RelHasWhite = RelationTypeRegistry.RelationTypeId("HAS_WHITE");
    private static readonly Hash128 RelHasBlack = RelationTypeRegistry.RelationTypeId("HAS_BLACK");
    private static readonly Hash128 RelHasSetup = RelationTypeRegistry.RelationTypeId("HAS_SETUP");
    private static readonly Hash128 RelHasClock = RelationTypeRegistry.RelationTypeId("HAS_CLOCK");
    private static readonly Hash128 RelHasEvalToken = RelationTypeRegistry.RelationTypeId("HAS_EVAL_TOKEN");
    private static readonly Hash128 RelMoveQuality = RelationTypeRegistry.RelationTypeId("MOVE_QUALITY");

    internal static NpgsqlDataSource? TryResolveDataSource(ISubstrateReader reader) =>
        reader is NpgsqlSubstrateReader npg ? npg.DataSource : null;

    // Witness sources whose recorded games the analyzer derives. Live/self-play games
    // (ChessSelfPlay source) fold their own outcomes at play time and must NOT be re-derived
    // here — that would double-count them.
    private static byte[][] WitnessSources() =>
    [
        ChessVocabulary.PgnSourceId.ToBytes(),
        ChessVocabulary.BookSourceId.ToBytes(),
    ];

    internal static async Task<long?> CountRecordedGamesAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(@"
            SELECT count(DISTINCT e.id)
            FROM laplace.entities e
            JOIN laplace.attestations mt
              ON mt.subject_id = e.id
             AND mt.type_id = $2
             AND mt.source_id = ANY($3::bytea[])
            WHERE e.type_id = $1");
        cmd.Parameters.AddWithValue(ChessVocabulary.GameType.ToBytes());
        cmd.Parameters.AddWithValue(RelHasMovetext.ToBytes());
        cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, WitnessSources());
        var total = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return total is long n ? n : 0L;
    }

    internal static async IAsyncEnumerable<Hash128> StreamUnanalyzedGameIdsAsync(
        NpgsqlDataSource ds,
        ISubstrateReader reader,
        int chunkSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        chunkSize = Math.Max(1, chunkSize);
        byte[] lastId = Array.Empty<byte>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var gameIds = await FetchRecordedGameIdPageAsync(ds, lastId, chunkSize * 4, ct)
                .ConfigureAwait(false);
            if (gameIds.Count == 0) yield break;

            lastId = gameIds[^1].ToBytes();

            for (int off = 0; off < gameIds.Count; off += chunkSize)
            {
                int take = Math.Min(chunkSize, gameIds.Count - off);
                var chunk = gameIds.GetRange(off, take);
                var markers = new Hash128[take];
                for (int i = 0; i < take; i++)
                    markers[i] = ChessVocabulary.AnalysisMarkerId(chunk[i], ChessAnalyze.Version);

                byte[] bm = await reader.EntitiesExistBitmapAsync(markers, ct).ConfigureAwait(false);
                long bits = (long)bm.Length * 8;
                for (int i = 0; i < take; i++)
                {
                    if (i < bits && (bm[i >> 3] & (1 << (i & 7))) != 0) continue;
                    yield return chunk[i];
                }
            }
        }
    }

    internal static async IAsyncEnumerable<ChessWitnessedGame> StreamUnanalyzedFromSubstrateAsync(
        NpgsqlDataSource ds,
        ISubstrateReader reader,
        int chunkSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        chunkSize = Math.Max(1, chunkSize);
        var idChunk = new List<Hash128>(chunkSize);
        await foreach (var gameId in StreamUnanalyzedGameIdsAsync(ds, reader, chunkSize, ct))
        {
            idChunk.Add(gameId);
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

    internal static async IAsyncEnumerable<Hash128> FilterUnanalyzedGameIdsAsync(
        IReadOnlyList<Hash128> gameIds, ISubstrateReader? reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (gameIds.Count == 0) yield break;
        if (reader is null) { foreach (var id in gameIds) yield return id; yield break; }

        var markers = new Hash128[gameIds.Count];
        for (int i = 0; i < gameIds.Count; i++)
            markers[i] = ChessVocabulary.AnalysisMarkerId(gameIds[i], ChessAnalyze.Version);

        byte[] bm = await reader.EntitiesExistBitmapAsync(markers, ct).ConfigureAwait(false);
        long bits = (long)bm.Length * 8;
        for (int i = 0; i < gameIds.Count; i++)
        {
            if (i < bits && (bm[i >> 3] & (1 << (i & 7))) != 0) continue;
            yield return gameIds[i];
        }
    }

    private static async Task<List<Hash128>> FetchRecordedGameIdPageAsync(
        NpgsqlDataSource ds, byte[] afterId, int limit, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(@"
            SELECT DISTINCT e.id
            FROM laplace.entities e
            JOIN laplace.attestations mt
              ON mt.subject_id = e.id
             AND mt.type_id = $2
             AND mt.source_id = ANY($3::bytea[])
            WHERE e.type_id = $1
              AND (octet_length($4) = 0 OR e.id > $4)
            ORDER BY e.id
            LIMIT $5");
        cmd.Parameters.AddWithValue(ChessVocabulary.GameType.ToBytes());
        cmd.Parameters.AddWithValue(RelHasMovetext.ToBytes());
        cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, WitnessSources());
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, afterId.Length == 0 ? Array.Empty<byte>() : afterId);
        cmd.Parameters.AddWithValue(limit);

        var ids = new List<Hash128>(limit);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            ids.Add(ReadHash(r, 0));
        return ids;
    }

    internal static async Task<IReadOnlyList<ChessWitnessedGame>> TryHydrateChunkAsync(
        NpgsqlDataSource ds, IReadOnlyList<Hash128> gameIds, CancellationToken ct)
    {
        if (gameIds.Count == 0) return Array.Empty<ChessWitnessedGame>();

        var gameBytes = new byte[gameIds.Count][];
        for (int i = 0; i < gameIds.Count; i++) gameBytes[i] = gameIds[i].ToBytes();

        var meta = new Dictionary<Hash128, GameMeta>(gameIds.Count);
        foreach (var id in gameIds) meta[id] = new GameMeta();

        await using (var cmd = ds.CreateCommand(@"
            SELECT a.subject_id, a.type_id, a.object_id
            FROM laplace.attestations a
            WHERE a.subject_id = ANY($1::bytea[])"))
        {
            cmd.Parameters.AddWithValue(gameBytes);
            cmd.Parameters[0].NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var gameId = ReadHash(r, 0);
                if (!meta.TryGetValue(gameId, out var gm)) continue;
                var type = ReadHash(r, 1);
                var obj = r.IsDBNull(2) ? default : ReadHash(r, 2);
                if (type == RelHasMovetext) gm.MovetextObj = obj;
                else if (type == RelHasWhite) gm.White = obj;
                else if (type == RelHasBlack) gm.Black = obj;
                else if (type == RelHasSetup) gm.SetupObj = obj;
                else if (type == RelHasResult) gm.ResultObj = obj;
            }
        }

        var contentIds = new List<Hash128>();
        void Need(Hash128 id) { if (id != default) contentIds.Add(id); }
        foreach (var gm in meta.Values)
        {
            Need(gm.MovetextObj);
            Need(gm.ResultObj);
            Need(gm.SetupObj);
        }

        var textById = await RenderTextBatchAsync(ds, contentIds, ct).ConfigureAwait(false);

        var plyIdToGamePly = new Dictionary<Hash128, (Hash128 Game, int Ply)>();
        var allPlyBytes = new List<byte[]>();
        foreach (var (gameId, gm) in meta)
        {
            if (gm.MovetextObj == default) continue;
            if (!textById.TryGetValue(gm.MovetextObj, out var movetext)
                || string.IsNullOrWhiteSpace(movetext)) continue;
            gm.Moves = movetext.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (gm.Moves.Length == 0) continue;
            gm.MoveCount = gm.Moves.Length;
            for (int ply = 0; ply < gm.MoveCount; ply++)
            {
                var plyId = ChessVocabulary.PlyId(gameId, ply);
                plyIdToGamePly[plyId] = (gameId, ply);
                allPlyBytes.Add(plyId.ToBytes());
            }
        }

        if (allPlyBytes.Count > 0)
        {
            var plyContentIds = new List<Hash128>();
            await using (var plyCmd = ds.CreateCommand(@"
                SELECT a.subject_id, a.type_id, a.object_id
                FROM laplace.attestations a
                WHERE a.subject_id = ANY($1::bytea[])"))
            {
                plyCmd.Parameters.AddWithValue(allPlyBytes.ToArray());
                plyCmd.Parameters[0].NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                await using var pr = await plyCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await pr.ReadAsync(ct).ConfigureAwait(false))
                {
                    var plyId = ReadHash(pr, 0);
                    if (!plyIdToGamePly.TryGetValue(plyId, out var gp)) continue;
                    if (!meta.TryGetValue(gp.Game, out var gm) || gm.MoveCount <= 0) continue;
                    var type = ReadHash(pr, 1);
                    var obj = pr.IsDBNull(2) ? default : ReadHash(pr, 2);
                    if (obj == default) continue;
                    plyContentIds.Add(obj);
                    if (type == RelHasClock) gm.ClockObj[gp.Ply] = obj;
                    else if (type == RelHasEvalToken) gm.EvalObj[gp.Ply] = obj;
                    else if (type == RelMoveQuality) gm.QualityObj[gp.Ply] = obj;
                }
            }

            var plyText = await RenderTextBatchAsync(ds, plyContentIds, ct).ConfigureAwait(false);
            foreach (var gm in meta.Values)
            {
                if (gm.MoveCount <= 0) continue;
                for (int ply = 0; ply < gm.MoveCount; ply++)
                {
                    if (gm.ClockObj[ply] is { } co && plyText.TryGetValue(co, out var ctok))
                        gm.ClockTokens[ply] = ctok;
                    if (gm.EvalObj[ply] is { } eo && plyText.TryGetValue(eo, out var etok))
                        gm.EvalTokens[ply] = etok;
                    if (gm.QualityObj[ply] is { } qo && plyText.TryGetValue(qo, out var qtok))
                        gm.QualityTokens[ply] = qtok;
                }
            }
        }

        var outList = new List<ChessWitnessedGame>(gameIds.Count);
        foreach (var gameId in gameIds)
        {
            if (!meta.TryGetValue(gameId, out var gm) || gm.Moves is null || gm.Moves.Length == 0) continue;
            string? resultStr = gm.ResultObj != default && textById.TryGetValue(gm.ResultObj, out var rs)
                ? rs : null;
            string? startFen = gm.SetupObj != default && textById.TryGetValue(gm.SetupObj, out var fen)
                ? fen : null;
            outList.Add(new ChessWitnessedGame(
                gameId, gm.Moves, ParseResult(resultStr),
                gm.White != default ? gm.White : null,
                gm.Black != default ? gm.Black : null,
                startFen,
                gm.AnyClock ? gm.ClockTokens : null,
                gm.AnyEval ? gm.EvalTokens : null,
                gm.AnyQual ? gm.QualityTokens : null));
        }
        return outList;
    }

    internal static async Task<ChessWitnessedGame?> TryHydrateAsync(
        NpgsqlDataSource ds, Hash128 gameId, CancellationToken ct)
    {
        var list = await TryHydrateChunkAsync(ds, [gameId], ct).ConfigureAwait(false);
        return list.Count > 0 ? list[0] : null;
    }

    private sealed class GameMeta
    {
        public Hash128 MovetextObj;
        public Hash128 White;
        public Hash128 Black;
        public Hash128 SetupObj;
        public Hash128 ResultObj;
        public string[]? Moves;
        public int MoveCount;
        public readonly Hash128?[] ClockObj = new Hash128?[512];
        public readonly Hash128?[] EvalObj = new Hash128?[512];
        public readonly Hash128?[] QualityObj = new Hash128?[512];
        public readonly string?[] ClockTokens = new string?[512];
        public readonly string?[] EvalTokens = new string?[512];
        public readonly string?[] QualityTokens = new string?[512];
        public bool AnyClock => ClockTokens.Any(t => t is not null);
        public bool AnyEval => EvalTokens.Any(t => t is not null);
        public bool AnyQual => QualityTokens.Any(t => t is not null);
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

        await using var cmd = ds.CreateCommand("SELECT laplace.render_text_batch($1, 48)");
        cmd.Parameters.AddWithValue(bytes);
        cmd.Parameters[0].NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        var o = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (o is not string[] texts || texts.Length != unique.Length) return map;
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

    private static Hash128 ReadHash(NpgsqlDataReader r, int ord)
    {
        var bytes = (byte[])r[ord];
        return Hash128.FromBytes(bytes);
    }
}
