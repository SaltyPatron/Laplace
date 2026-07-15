using System.Runtime.CompilerServices;
using System.Text;
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
/// analysis-version marker. Batch hydration + render_text_batch — no PGN files. Per-ply
/// tokens (SAN/clock/eval/quality) are re-parsed from each game's verbatim HAS_MOVETEXT
/// content, the single lossless per-game record.
/// </summary>
internal static class ChessWitnessHydrator
{
    private static readonly Hash128 RelHasMovetext = RelationTypeRegistry.RelationTypeId("HAS_MOVETEXT");
    private static readonly Hash128 RelHasResult = RelationTypeRegistry.RelationTypeId("HAS_RESULT");
    private static readonly Hash128 RelHasWhite = RelationTypeRegistry.RelationTypeId("HAS_WHITE");
    private static readonly Hash128 RelHasBlack = RelationTypeRegistry.RelationTypeId("HAS_BLACK");
    private static readonly Hash128 RelHasSetup = RelationTypeRegistry.RelationTypeId("HAS_SETUP");

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

        var outList = new List<ChessWitnessedGame>(gameIds.Count);
        foreach (var gameId in gameIds)
        {
            if (!meta.TryGetValue(gameId, out var gm) || gm.MovetextObj == default) continue;
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
                gameId, moves, ParseResult(resultStr),
                gm.White != default ? gm.White : null,
                gm.Black != default ? gm.Black : null,
                startFen, clockTokens, evalTokens, qualityTokens));
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
        NpgsqlDataSource ds, Hash128 gameId, CancellationToken ct)
    {
        var list = await TryHydrateChunkAsync(ds, [gameId], ct).ConfigureAwait(false);
        return list.Count > 0 ? list[0] : null;
    }

    // Per-game witnessed scaffold: game-level attestation objects only. Per-ply annotations are
    // NOT read from attestations — they are re-parsed from the verbatim movetext (ParseMovetext).
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
