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
public sealed class ChessPgnDecomposer(bool recursive = false) : ComposeDecomposer<ChessGameRecord>
{
    private readonly SearchOption _scope =
        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

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
                           ecosystemPath, _scope, ContainmentReader, ws.Batch, options.ReObservePresent, ct))
            yield return game;
    }

    protected override void Compose(ChessGameRecord record, SubstrateChangeBuilder b) => RecordGame(record, b);

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
        var gameId = ChessVocabulary.GameId(whiteName, blackName, date, moves);
        return new ChessGameRecord(gameText, moves, result, gameId)
        {
            Walk = walk,
            WhiteName = whiteName,
            BlackName = blackName,
            Date = date,
        };
    }

    // ---- RECORDER: witnessed transcription only. No board replay, no move generation, no
    // geometry, no consensus. Transcribes exactly what the PGN asserts. Everything derived
    // (positions, motifs, opening classification, the Glicko fold) is the analyzer's job
    // (ChessAnalyze), run later off this witnessed layer. See .scratchpad/08_Record_vs_Calculate.
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
        var (gameText, _, result, gameId) = parsed;
        var src = sourceId ?? ChessVocabulary.PgnSourceId;

        var (whiteElo, blackElo) = ParseElos(gameText);
        // TryParseGame already scanned these header tags; only re-scan for records built elsewhere.
        var (whiteName, blackName) = parsed.WhiteName is { } wn
            ? (wn, parsed.BlackName!)
            : ParseNames(gameText);
        string date = parsed.Date ?? PgnGames.TagStr(gameText, "Date");
        var whitePlayer = EmitPlayer(b, whiteName, src);
        var blackPlayer = EmitPlayer(b, blackName, src);

        EmitGame(b, gameId, gameText, date, result, whitePlayer, blackPlayer, whiteElo, blackElo, src);
        RecordStartPosition(b, gameId, gameText, src);
        RecordOpeningHeaders(b, gameId, gameText, src);
        RecordMovetext(b, gameId, gameText, src);
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

    // Witness the VERBATIM PGN movetext (clocks, evals, comments, NAGs, result token — the
    // bytes the source asserted) as one content edge on the game. This is the lossless
    // record: every per-ply token is reconstructible from it (MovetextTokens.Parse).
    private static void RecordMovetext(SubstrateChangeBuilder b, Hash128 gameId, string gameText, Hash128 src)
    {
        string movetext = MovetextSection(gameText);
        if (movetext.Length == 0) return;
        if (ContentEmitter.Emit(b, movetext, src) is { } mtId)
            b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_MOVETEXT", mtId, src, null, PgnWitnessWeight));
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
        SubstrateChangeBuilder b, Hash128 gameId, string gameText, string date, GameOutcome result,
        Hash128? whitePlayer, Hash128? blackPlayer, int whiteElo, int blackElo, Hash128 src)
    {
        b.AddEntity(gameId, EntityTier.Document, ChessVocabulary.GameType, src);

        if (whitePlayer is { } wp) b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_WHITE", wp, src, null, PgnWitnessWeight));
        if (blackPlayer is { } bp) b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_BLACK", bp, src, null, PgnWitnessWeight));

        Meta(b, gameId, "HAS_EVENT", PgnGames.TagStr(gameText, "Event"), src);
        Meta(b, gameId, "ON_DATE", date, src);
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
                    "ChessPgnDecomposer: failed to estimate games in {File}: {Message}", f, ex.Message);
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

    // Header tags TryParseGame already scanned, threaded through so RecordGame does not
    // re-scan the full game text. Null when the record was built without a header pass.
    internal string? WhiteName { get; init; }
    internal string? BlackName { get; init; }
    internal string? Date { get; init; }

    public Hash128 TrunkRootId => GameId;
}
