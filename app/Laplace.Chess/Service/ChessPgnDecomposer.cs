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

public sealed class ChessPgnDecomposer : ComposeDecomposer<ChessGameRecord>
{
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
    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.PgnSourceId, SourceName, ChessVocabulary.PgnTrustClass, ct);

    protected override async IAsyncEnumerable<ChessGameRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options, DefaultBatchSize);
        await foreach (var game in StreamNovelGamesAsync(
                           ecosystemPath, ContainmentReader, ws.Batch, options.ReObservePresent, ct))
            yield return game;
    }

    protected override void Compose(ChessGameRecord record, SubstrateChangeBuilder b) => RecordGame(record, b);

    private static async IAsyncEnumerable<ChessGameRecord> StreamNovelGamesAsync(
        string ecosystemPath, ISubstrateReader? reader, int chunkSize, bool reObservePresent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var chunk = new List<ChessGameRecord>(chunkSize);
        await foreach (var gameText in StreamAllGamesAsync(ecosystemPath, ct))
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
            if (reader.IsProvenPresent(chunk[i].GameId)) continue;
            toProbe.Add(i);
        }

        bool[] present = new bool[chunk.Count];
        if (toProbe.Count > 0)
        {
            var ids = new Hash128[toProbe.Count];
            for (int k = 0; k < toProbe.Count; k++) ids[k] = chunk[toProbe[k]].GameId;
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
            if (!present[i] && !reader.IsProvenPresent(chunk[i].GameId))
                yield return chunk[i];
    }

    internal static async IAsyncEnumerable<string> StreamAllGamesAsync(
        string ecosystemPath, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var file in EnumerateFiles(ecosystemPath))
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
        var gameId = ChessVocabulary.GameId(whiteName, blackName, date, moves);
        return new ChessGameRecord(gameText, moves, result, gameId) { Walk = walk };
    }

    // ---- RECORDER: witnessed transcription only. No board replay, no move generation, no
    // geometry, no consensus. Transcribes exactly what the PGN asserts. Everything derived
    // (positions, motifs, opening classification, the Glicko fold) is the analyzer's job
    // (ChessAnalyze), run later off this witnessed layer. See .scratchpad/08_Record_vs_Calculate.
    internal static void RecordGame(ChessGameRecord parsed, SubstrateChangeBuilder b)
    {
        var (gameText, moves, result, gameId) = parsed;
        var walk = parsed.Walk;
        var src = ChessVocabulary.PgnSourceId;

        var (whiteElo, blackElo) = ParseElos(gameText);
        var (whiteName, blackName) = ParseNames(gameText);
        var whitePlayer = EmitPlayer(b, whiteName);
        var blackPlayer = EmitPlayer(b, blackName);

        EmitGame(b, gameId, gameText, result, whitePlayer, blackPlayer, whiteElo, blackElo);
        RecordStartPosition(b, gameId, gameText, src);
        RecordOpeningHeaders(b, gameId, gameText, src);
        RecordMovetext(b, gameId, moves, src);
        RecordPlyAnnotations(b, gameId, gameText, walk, moves.Count, src);
    }

    private static void RecordStartPosition(SubstrateChangeBuilder b, Hash128 gameId, string gameText, Hash128 src)
    {
        if (PgnGames.TagStr(gameText, "SetUp") != "1") return;
        string fen = PgnGames.TagStr(gameText, "FEN");
        if (string.IsNullOrWhiteSpace(fen)) return;
        if (ContentEmitter.Emit(b, fen, src) is { } fid)
            b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_SETUP", fid, src, null, PgnWitnessWeight));
    }

    private static void RecordOpeningHeaders(SubstrateChangeBuilder b, Hash128 gameId, string gameText, Hash128 src)
    {
        string eco = ChessCanonical.Eco(PgnGames.TagStr(gameText, "ECO")) ?? "";
        if (eco.Length > 0) ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_ECO", eco, PgnWitnessWeight, src);
        string opening = ChessCanonical.OpeningName(PgnGames.TagStr(gameText, "Opening")) ?? "";
        if (opening.Length > 0) ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_OPENING", opening, PgnWitnessWeight, src);
    }

    private static void RecordMovetext(SubstrateChangeBuilder b, Hash128 gameId, IReadOnlyList<string> moves, Hash128 src)
    {
        if (moves.Count == 0) return;
        if (ContentEmitter.Emit(b, string.Join(' ', moves), src) is { } mtId)
            b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_MOVETEXT", mtId, src, null, PgnWitnessWeight));
    }

    private static void RecordPlyAnnotations(
        SubstrateChangeBuilder b, Hash128 gameId, string gameText,
        PgnMovetext.PgnWalkResult walk, int moveCount, Hash128 src)
    {
        var clockTokens = PgnClocks.ClockTokens(gameText, moveCount);
        var evalTokens = PgnEvals.EvalTokens(gameText, moveCount);
        var mainline = walk.Mainline;
        for (int ply = 0; ply < mainline.Count; ply++)
        {
            var pm = mainline[ply];
            string? clk = clockTokens is not null && ply < clockTokens.Length ? clockTokens[ply] : null;
            string? ev = evalTokens is not null && ply < evalTokens.Length ? evalTokens[ply] : null;
            string? comment = string.IsNullOrWhiteSpace(pm.CommentText) ? null : pm.CommentText;
            string? quality = MoveQuality.FromStream(pm);
            if (clk is null && ev is null && comment is null && quality is null) continue;

            var plyId = ChessVocabulary.PlyId(gameId, ply);
            b.AddEntity(plyId, EntityTier.Word, ChessVocabulary.PlyType, src);
            b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_PLY", plyId, src, gameId, PgnWitnessWeight));
            PlyMeta(b, plyId, "HAS_SAN", pm.San, src, gameId);
            PlyMeta(b, plyId, "HAS_CLOCK", clk, src, gameId);
            PlyMeta(b, plyId, "HAS_EVAL_TOKEN", ev, src, gameId);
            PlyMeta(b, plyId, "MOVE_QUALITY", quality, src, gameId);
            PlyMeta(b, plyId, "HAS_COMMENT", comment, src, gameId);
        }
    }

    private static void PlyMeta(
        SubstrateChangeBuilder b, Hash128 ply, string rel, string? value, Hash128 src, Hash128 ctx)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (ContentEmitter.Emit(b, value, src) is { } vid)
            b.AddAttestation(NativeAttestation.Categorical(ply, rel, vid, src, ctx, PgnWitnessWeight));
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
        SubstrateChangeBuilder b, Hash128 gameId, string gameText, GameOutcome result,
        Hash128? whitePlayer, Hash128? blackPlayer, int whiteElo, int blackElo)
    {
        var src = ChessVocabulary.PgnSourceId;
        b.AddEntity(gameId, EntityTier.Document, ChessVocabulary.GameType, src);

        if (whitePlayer is { } wp) b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_WHITE", wp, src, null, PgnWitnessWeight));
        if (blackPlayer is { } bp) b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_BLACK", bp, src, null, PgnWitnessWeight));

        Meta(b, gameId, "HAS_EVENT", PgnGames.TagStr(gameText, "Event"), src);
        Meta(b, gameId, "ON_DATE", PgnGames.TagStr(gameText, "Date"), src);
        Meta(b, gameId, "HAS_ECO", PgnGames.TagStr(gameText, "ECO"), src);
        Meta(b, gameId, "HAS_TERMINATION", PgnGames.TagStr(gameText, "Termination"), src);
        Meta(b, gameId, "HAS_RESULT", result.IsDraw ? "1/2-1/2" : result.Winner == 0 ? "1-0" : "0-1", src);

        string tc = PgnGames.TagStr(gameText, "TimeControl");
        Meta(b, gameId, "HAS_TIME_CONTROL", tc, src);
        Meta(b, gameId, "HAS_TC_CLASS", TcClass(tc), src);

        if (whitePlayer is { } wp2 && whiteElo > 0) Rating(b, wp2, whiteElo, gameId, src);
        if (blackPlayer is { } bp2 && blackElo > 0) Rating(b, bp2, blackElo, gameId, src);
    }

    private static void Meta(SubstrateChangeBuilder b, Hash128 game, string rel, string value, Hash128 src)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "?" || value == "-" || value == "????.??.??") return;
        if (ContentEmitter.Emit(b, value, src) is { } vid)
            b.AddAttestation(NativeAttestation.Categorical(game, rel, vid, src, null, PgnWitnessWeight));
    }

    private static void Rating(SubstrateChangeBuilder b, Hash128 player, int elo, Hash128 game, Hash128 src)
    {
        if (ContentEmitter.Emit(b, elo.ToString(), src) is { } rid)
            b.AddAttestation(NativeAttestation.Categorical(player, "HAS_RATING", rid, src, game, PgnWitnessWeight));
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

    private static Hash128? EmitPlayer(SubstrateChangeBuilder b, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "?") return null;
        var canonicalId = ChessVocabulary.PlayerId(name);
        ChessVocabulary.EmitPlayer(b, canonicalId, name, ChessVocabulary.PgnSourceId);
        var legacyId = ChessVocabulary.LegacyPlayerId(name);
        if (legacyId != canonicalId)
            b.AddAttestation(NativeAttestation.Categorical(
                canonicalId, "CORRESPONDS_TO", legacyId, ChessVocabulary.PgnSourceId, null, PgnWitnessWeight));
        return canonicalId;
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long games = 0;
        foreach (var f in EnumerateFiles(context.EcosystemPath))
        {
            try
            {
                using var r = new StreamReader(f);
                string? line;
                while ((line = r.ReadLine()) is not null)
                    if (line.StartsWith("[Event ", StringComparison.Ordinal)) games++;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "ChessPgnDecomposer: failed to estimate games in {File}: {Message}", f, ex.Message);
            }
        }
        return Task.FromResult<long?>(games == 0 ? null : games);
    }

    private static IEnumerable<string> EnumerateFiles(string path)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        if (File.Exists(path)) { yield return Path.GetFullPath(path); yield break; }
        if (!Directory.Exists(path)) yield break;
        foreach (var f in Directory.EnumerateFiles(path, "*.pgn", SearchOption.AllDirectories)
                                   .OrderBy(p => p, StringComparer.Ordinal))
            yield return f;
    }
}

/// <summary>
/// Parsed PGN game with content-addressed <see cref="GameId"/> for existence-gate short-circuit.
/// </summary>
public sealed record ChessGameRecord(
    string GameText,
    List<string> Moves,
    GameOutcome Result,
    Hash128 GameId)
    : ITrunkRootRecord
{
    internal PgnMovetext.PgnWalkResult Walk { get; init; } = null!;
    public Hash128 TrunkRootId => GameId;
}
