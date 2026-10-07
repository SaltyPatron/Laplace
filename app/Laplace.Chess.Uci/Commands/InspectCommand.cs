using System.Text;
using System.Text.RegularExpressions;
using Laplace.Chess.Service;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Uci.Commands;

public sealed record AtomView(string Domain, int Value, string Meaning, string Bytes, string Id);

public sealed record PositionView(string Id, string LawId, string Fen, string SideToMove, string Castling, string? RookOverride,
    string EnPassant, string Rules, int PieceSquares, IReadOnlyList<AtomView> Atoms);

public sealed record PlyView(int Ply, string San, string Uci, string MoveId, IReadOnlyList<AtomView> MoveAtoms, string To,
    string TransitionKey, string? Comment, string? Annotation, PositionView? Position);

public sealed record IdentityView(string Kind, string Id, string HashedFrom, string? Preimage, bool? Verified, string? LawId, string? Note);

public sealed record PlayerView(string Side, string NameAsWritten, string Folded, string Id, string Preimage, bool Verified);

public sealed record HeaderView(string Tag, string Value, string Summary, IReadOnlyList<string> Dispositions);

public sealed record EntityView(string Id, string Role, string Type, int Tier);

public sealed record ClaimView(string Subject, string Relation, string? Object, string? Context, string? Qualifier);

public sealed record GameInspection(
    int Index, IReadOnlyList<string> RecordKinds, bool Stored, string Disposition, IReadOnlyList<HeaderView> Headers,
    PositionView Start, IReadOnlyList<PlyView> Plies, PositionView Final, IdentityView? Line, IdentityView? Playing,
    IdentityView? Event, IReadOnlyList<PlayerView> Players, IReadOnlyList<EntityView> Entities, IReadOnlyList<ClaimView> Claims,
    IReadOnlyDictionary<string, int> OtherClaims, IReadOnlyList<string> Findings);

public sealed record InspectReport(string Input, string Kind, string Statement, IReadOnlyList<GameInspection> Games);

/// <summary>
/// <c>inspect &lt;pgn|fen|moves&gt;</c>: the tree the monorepo would store for the input, read-only and without a database.
/// It runs the monorepo's own code (ChessPgnDecomposer.TryParseGame and RecordGame into an unapplied change,
/// ChessPositionIdentity, ContentEmitter) and shows, per position, its identity parts; each transition; the LINE and
/// the PLAYING (occurrence) identities and what each is hashed from; every header and whether it is stored as content,
/// hashed into an identity or dropped; and the record kind. Its findings name the differences from the law that
/// SaltyPatron/Laplace-Engine#46 lists.
/// </summary>
public static class InspectCommand
{
    public const string Statement =
        "What the monorepo's chess ingest (ChessPgnDecomposer) would store for this input, computed by its own code; nothing is written. "
        + "Findings cite SaltyPatron/Laplace-Engine#46; 'law' identities are BLAKE3 over the same children without the native Merkle's domain byte.";

    public static InspectReport Run(ChessCommandLine cmd)
    {
        string input = cmd.Positionals.Count > 0 ? string.Join(' ', cmd.Positionals)
            : throw new ArgumentException("inspect <pgn-file | FEN | moves> [--game N | --where Tag=Value] [--max-games N] [--fen F] [--json] [--all-positions]");
        bool all = cmd.Flag("all-positions");
        // Content identities resolve against the tier-0 ROM (the codepoint perfcache), mapped read-only; no database.
        CodepointPerfcache.LoadDefault();
        if (cmd.Positionals.Count == 1 && File.Exists(input))
            return new InspectReport(input, "pgn", Statement, SelectGames(input, cmd).Select(g => InspectGame(g.Index, g.Text, all)).ToList());
        if (LooksLikeFen(input))
            return new InspectReport(input, "fen", Statement, [InspectLine(input, cmd.Value("moves") ?? "", all)]);
        return new InspectReport(input, "moves", Statement, [InspectLine(cmd.Value("fen"), input, all)]);
    }

    private static bool LooksLikeFen(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0].Count(static c => c == '/') == 7 && parts[1] is "w" or "b";
    }

    private static IEnumerable<(int Index, string Text)> SelectGames(string path, ChessCommandLine cmd)
    {
        int max = cmd.Int("max-games", 1);
        int? only = cmd.Value("game") is { } g ? int.Parse(g, System.Globalization.CultureInfo.InvariantCulture) : null;
        string? whereTag = null, whereValue = null;
        if (cmd.Value("where") is { } w)
        {
            int eq = w.IndexOf('=');
            if (eq <= 0) throw new ArgumentException("--where takes Tag=Value");
            (whereTag, whereValue) = (w[..eq], w[(eq + 1)..]);
        }
        int index = 0, taken = 0;
        foreach (var text in PgnGames.StreamGames(path))
        {
            index++;
            if (only is { } n && index != n) continue;
            if (whereTag is not null && PgnGames.TagStr(text, whereTag) != whereValue) continue;
            yield return (index, text);
            if (++taken >= (only is null ? max : 1)) yield break;
        }
        if (taken == 0) throw new ArgumentException($"no game in {path} matches the selection");
    }

    private static string Hex(Hash128 id) => Convert.ToHexStringLower(id.ToBytes());

    private static string LawId(ReadOnlySpan<Hash128> children)
    {
        if (children.Length == 1) return Hex(children[0]);
        var bytes = new byte[children.Length * 16];
        for (int i = 0; i < children.Length; i++) children[i].WriteBytes(bytes.AsSpan(i * 16));
        return Hex(Hash128.Blake3(bytes));
    }

    // ---- positions and moves ----

    private static readonly string[] PieceLetters = ["P", "N", "B", "R", "Q", "K", "p", "n", "b", "r", "q", "k"];

    private static string SquareName(int bit) => $"{(char)('a' + (bit & 7))}{(bit >> 3) + 1}";

    private static AtomView Atom(ChessPositionIdentity.Atom atom)
    {
        Span<byte> bytes = stackalloc byte[33];
        int n = ChessPositionIdentity.FillAtomBytes(atom, bytes);
        string hex = string.Join(' ', bytes[..n].ToArray().Select(static b => $"{b:x2}"));
        var (domain, meaning) = atom.Domain switch
        {
            ChessPositionIdentity.SideDomain => ("side", atom.Value == 1 ? "white to move" : "black to move"),
            ChessPositionIdentity.CastlingDomain => ("castling", CastlingText((CastleRights)atom.Value)),
            ChessPositionIdentity.CastlingRookOverrideDomain => ("castling-rook-override", RookOverrideText(atom.Value)),
            ChessPositionIdentity.EnPassantDomain => ("en-passant", atom.Value == 64 ? "none" : SquareName(atom.Value)),
            ChessPositionIdentity.PieceSquareDomain => ("piece-square", PieceLetters[atom.Value >> 6] + SquareName(atom.Value & 63)),
            ChessPositionIdentity.RulesDomain => ("rules", "BLAKE3 of the ruleset's label " + Hex(atom.Digest)),
            ChessPositionIdentity.MovePieceDomain => ("move-piece", PieceLetters[atom.Value]),
            ChessPositionIdentity.MoveFromDomain => ("move-from", SquareName(atom.Value)),
            ChessPositionIdentity.MoveToDomain => ("move-to", SquareName(atom.Value)),
            ChessPositionIdentity.MoveFlagsDomain => ("move-flags", ((MoveFlags)atom.Value).ToString()),
            ChessPositionIdentity.MovePromotionDomain => ("move-promotion", atom.Value switch { 0 => "none", 1 => "N", 2 => "B", 3 => "R", _ => "Q" }),
            _ => ($"domain-{atom.Domain}", atom.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        return new AtomView(domain, atom.Value, meaning, hex, Hex(ChessPositionIdentity.AtomId(atom)));
    }

    private static string CastlingText(CastleRights rights)
    {
        if (rights == CastleRights.None) return "0000 (none)";
        var parts = new List<string>();
        if ((rights & CastleRights.WhiteKing) != 0) parts.Add("White g-side");
        if ((rights & CastleRights.WhiteQueen) != 0) parts.Add("White c-side");
        if ((rights & CastleRights.BlackKing) != 0) parts.Add("Black g-side");
        if ((rights & CastleRights.BlackQueen) != 0) parts.Add("Black c-side");
        return $"{Convert.ToString((int)rights, 2).PadLeft(4, '0')} ({string.Join(", ", parts)})";
    }

    private static string RookOverrideText(int packed)
    {
        string Files(int bits) => string.Concat(Enumerable.Range(0, 8).Where(f => (bits & (1 << f)) != 0).Select(static f => (char)('a' + f)));
        return $"designated rook files White {Files(packed & 0xFF)} Black {Files(packed >> 8)}";
    }

    /// <summary>27.1's reading (an inference, labelled as such): per colour, an 8-bit file mask with the king's file and each
    /// castling rook's file set.</summary>
    private static string WikiCastlingMask(Board board)
    {
        string Side(bool white, CastleRights k, CastleRights q, int kingRook, int queenRook)
        {
            int king = board.FindKing(white);
            if (king < 0 || (board.Castle & (k | q)) == 0) return "00000000";
            int mask = 1 << Board.FileOf(king);
            if ((board.Castle & k) != 0) mask |= 1 << kingRook;
            if ((board.Castle & q) != 0) mask |= 1 << queenRook;
            return new string(Enumerable.Range(0, 8).Select(f => (mask & (1 << f)) != 0 ? (char)('a' + f) : '.').ToArray());
        }
        return $"White {Side(true, CastleRights.WhiteKing, CastleRights.WhiteQueen, board.WhiteKingRookFile, board.WhiteQueenRookFile)}"
            + $" Black {Side(false, CastleRights.BlackKing, CastleRights.BlackQueen, board.BlackKingRookFile, board.BlackQueenRookFile)}";
    }

    public static PositionView Position(Board board)
    {
        ChessCompose.InitializePerfcaches();
        Span<ChessPositionIdentity.Atom> atoms = stackalloc ChessPositionIdentity.Atom[ChessPositionIdentity.MaxAtoms];
        int n = ChessPositionIdentity.FillAtoms(board, ChessVariantRules.Standard, atoms);
        var views = new List<AtomView>(n);
        var ids = new Hash128[n];
        for (int i = 0; i < n; i++)
        {
            views.Add(Atom(atoms[i]));
            ids[i] = ChessPositionIdentity.AtomId(atoms[i]);
        }
        var id = ChessCompose.PositionId(board);
        return new PositionView(Hex(id), LawId(ids), board.ToFen(),
            views.First(static a => a.Domain == "side").Meaning,
            views.First(static a => a.Domain == "castling").Meaning + "; 27.1 reading (inference): " + WikiCastlingMask(board),
            views.FirstOrDefault(static a => a.Domain == "castling-rook-override")?.Meaning,
            views.First(static a => a.Domain == "en-passant").Meaning,
            views.Any(static a => a.Domain == "rules") ? "rules atom present" : "no rules atom (ChessPgnDecomposer passes no ruleset)",
            views.Count(static a => a.Domain == "piece-square"), views);
    }

    private static (string Id, List<AtomView> Atoms) Move(Piece moving, ChessMove move)
    {
        Span<ChessPositionIdentity.Atom> atoms = stackalloc ChessPositionIdentity.Atom[5];
        int n = ChessPositionIdentity.FillMoveAtoms(moving, move, atoms);
        var views = new List<AtomView>(n);
        for (int i = 0; i < n; i++) views.Add(Atom(atoms[i]));
        return (Hex(ChessCompose.MoveId(moving, move)), views);
    }

    private sealed record Replay(Board Start, List<(string San, ChessMove Move, Piece Moving, Hash128 MoveId, Board After)> Plies);

    /// <summary>The trajectory: SAN or UCI tokens from a start board, each resolved against the legal moves.</summary>
    private static Replay? ReplayTokens(Board start, IEnumerable<string> tokens, out string? error)
    {
        error = null;
        var board = start.Clone();
        var plies = new List<(string, ChessMove, Piece, Hash128, Board)>();
        var scratch = new List<ChessMove>(64);
        foreach (var token in tokens)
        {
            var mv = San.Resolve(board, token, scratch)
                ?? MoveGen.Legal(board).Cast<ChessMove?>().FirstOrDefault(m => m!.Value.ToUci() == token);
            if (mv is null) { error = $"'{token}' is not a legal move at ply {plies.Count + 1}"; return null; }
            Piece moving = board.Squares[mv.Value.From];
            string san = San.ToSan(board, mv.Value);
            var id = ChessCompose.MoveId(moving, mv.Value);
            board = board.Clone();
            MoveApply.Make(board, mv.Value);
            plies.Add((san, mv.Value, moving, id, board));
        }
        return new Replay(start, plies);
    }

    private static List<PlyView> PlyViews(Replay replay, IReadOnlyList<PgnMovetext.PgnMoveStream>? mainline, bool all)
    {
        var views = new List<PlyView>();
        var from = ChessCompose.PositionId(replay.Start);
        for (int i = 0; i < replay.Plies.Count; i++)
        {
            var (san, move, moving, moveId, after) = replay.Plies[i];
            var to = ChessCompose.PositionId(after);
            var (mid, atoms) = Move(moving, move);
            string? comment = null, annotation = null;
            if (mainline is not null && i < mainline.Count)
            {
                var p = mainline[i];
                comment = string.IsNullOrWhiteSpace(p.CommentText) ? null : p.CommentText;
                var parts = new[] { p.Nag is { } nag ? $"${nag}" : null, p.StandaloneAnnotation, p.SuffixAnnotation }.Where(static s => !string.IsNullOrWhiteSpace(s));
                annotation = string.Join(' ', parts) is { Length: > 0 } a ? a : null;
            }
            views.Add(new PlyView(i + 1, san, move.ToUci(), mid, atoms, Hex(to), Hex(ChessCompose.TransitionKey(from, moveId)), comment, annotation,
                all ? Position(after) : null));
            from = to;
        }
        return views;
    }

    private static IdentityView LineView(Hash128 p0, IReadOnlyList<Hash128> moveIds, Hash128? recorded)
    {
        var children = new Hash128[moveIds.Count + 1];
        children[0] = p0;
        for (int i = 0; i < moveIds.Count; i++) children[i + 1] = moveIds[i];
        var id = ChessCompose.LineId(p0, moveIds.ToArray());
        return new IdentityView("LINE", Hex(id),
            moveIds.Count == 0 ? "no moves: a one-child composition is its child, so the line IS its start position P0"
                : $"Merkle over [P0, M1..M{moveIds.Count}] ({moveIds.Count + 1} children) = BLAKE3(0x01 ‖ child ids)",
            null, recorded is null ? null : recorded == id, LawId(children),
            "content: the start position and the ordered moves; identical play is one line whoever played it");
    }

    // ---- a bare line (FEN and/or moves) ----

    private static GameInspection InspectLine(string? fen, string moves, bool all)
    {
        var start = string.IsNullOrWhiteSpace(fen) || fen == "startpos" ? new ChessModality().Initial().Board : Board.FromFen(fen);
        var tokens = Regex.Replace(moves, @"\d+\.(\.\.)?", " ").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Where(static t => t is not ("1-0" or "0-1" or "1/2-1/2" or "*")).ToList();
        var replay = ReplayTokens(start, tokens, out var error) ?? throw new ArgumentException(error);
        var line = LineView(ChessCompose.PositionId(start), replay.Plies.Select(static p => p.MoveId).ToList(), null);
        var findings = CommonFindings(replay, line, null, []);
        findings.Insert(0, "A bare line has no occurrence: the monorepo stores a line only under a recorded game (a PLAYING), so this shows the content alone.");
        return new GameInspection(1, ["line (no PGN record)"], false, "not a record: positions, moves and the line only", [],
            Position(start), PlyViews(replay, null, all), Position(replay.Plies.Count > 0 ? replay.Plies[^1].After : start),
            line, null, null, [], [], [], new Dictionary<string, int>(), findings);
    }

    // ---- a PGN game ----

    private static readonly Regex HeaderLine = new(@"^\[(\w+)\s+""((?:[^""\\]|\\.)*)""\]\s*$", RegexOptions.Multiline);
    private static readonly Regex ClockOnly = new(@"^(\s*\[%(clk|emt|eval|timestamp)[^\]]*\]\s*)+$");

    public static GameInspection InspectGame(int index, string gameText, bool all = false)
    {
        ChessCompose.InitializePerfcaches();
        var headers = HeaderLine.Matches(gameText.Split("\n\n", 2)[0]).Select(static m => (Tag: m.Groups[1].Value, Value: m.Groups[2].Value)).ToList();
        var bytes = Encoding.UTF8.GetBytes(gameText);
        PgnMovetext.PgnWalkResult walk;
        using (var ast = GrammarDecomposer.Parse(bytes, "pgn")) walk = PgnMovetext.Walk(ast, bytes);
        string Tag(string t) => PgnGames.TagStr(gameText, t);

        // record kind, as Laplace-Wiki Corpora/Games.md names them
        var kinds = new List<string>();
        string? startFen = Tag("SetUp") == "1" ? Tag("FEN") : null;
        string variant = Tag("Variant");
        bool incomplete = walk.Result is null;
        if (incomplete) kinds.Add($"incomplete (Result \"{Tag("Result")}\")");
        if (Tag("Termination") is var term && Regex.IsMatch(term, "abandon|forfeit", RegexOptions.IgnoreCase)) kinds.Add($"incomplete (Termination \"{term}\")");
        if (startFen is not null) kinds.Add("custom start (SetUp/FEN)");
        if (variant.Length > 0 && !variant.Equals("Standard", StringComparison.OrdinalIgnoreCase)) kinds.Add($"variant ({variant})");
        int variations = walk.AllPlies.Count(static p => p.InVariation);
        var comments = walk.Mainline.Where(static p => !string.IsNullOrWhiteSpace(p.CommentText)).ToList();
        int clockComments = comments.Count(c => ClockOnly.IsMatch(c.CommentText!));
        int textComments = comments.Count - clockComments;
        int nags = walk.AllPlies.Count(static p => p.Nag is not null || p.StandaloneAnnotation is not null || p.SuffixAnnotation is not null);
        if (variations > 0 || textComments > 0 || nags > 0)
            kinds.Add($"study / annotated ({variations} variation plies, {textComments} text comments, {nags} glyphs)");
        if (kinds.Count == 0 || kinds.All(static k => k.StartsWith("custom", StringComparison.Ordinal) || k.StartsWith("variant", StringComparison.Ordinal)))
            kinds.Insert(0, "played game");
        if (clockComments > 0) kinds.Add($"{clockComments} clock comments ([%clk])");

        var start = startFen is not null ? Board.FromFen(startFen) : new ChessModality().Initial().Board;
        var replay = ReplayTokens(start, walk.Mainline.Select(static p => p.San), out var replayError);
        var record = ChessPgnDecomposer.TryParseGame(gameText);

        if (record is null || replay is null)
        {
            string reason = walk.Result is null
                ? "dropped by ChessPgnDecomposer.TryParseGame: no finished result, so nothing of the record is stored, the trajectory included"
                : $"dropped by ChessPgnDecomposer.TryParseGame: the main line does not replay ({replayError ?? "start or SAN unreadable"})";
            var lineView = replay is null ? null : LineView(ChessCompose.PositionId(start), replay.Plies.Select(static p => p.MoveId).ToList(), null);
            var dropFindings = new List<string> { "#46 record kinds: " + reason + "; the law keeps the trajectory as content and claims no outcome." };
            if (replay is not null && lineView is not null) dropFindings.AddRange(CommonFindings(replay, lineView, variant, []));
            return new GameInspection(index, kinds, false, reason,
                headers.Select(static h => new HeaderView(h.Tag, h.Value, "dropped", ["dropped: the whole record is dropped"])).ToList(),
                Position(start), replay is null ? [] : PlyViews(replay, walk.Mainline, all),
                Position(replay is { Plies.Count: > 0 } ? replay.Plies[^1].After : start), lineView, null, null, [], [], [],
                new Dictionary<string, int>(), dropFindings);
        }

        // Content the recorder may emit, named by value so a claim reads as what it says.
        var contentOf = new Dictionary<Hash128, string>();
        void Content(string? value)
        {
            if (string.IsNullOrEmpty(value) || ContentEmitter.RootId(value) is not { } id) return;
            contentOf.TryAdd(id, value);
        }
        foreach (var (_, value) in headers) Content(value);
        Content(ChessCanonical.Eco(Tag("ECO")));
        Content(ChessCanonical.OpeningName(Tag("Opening")));
        Content(ChessPgnDecomposer.TcClass(Tag("TimeControl")));
        Content(record.Result.ResultToken);

        // The monorepo's own recorder, into a change that is never applied.
        var full = Record(record, contentOf)!;
        string white = record.WhiteName ?? Tag("White"), black = record.BlackName ?? Tag("Black"), date = record.Date ?? Tag("Date");
        string ev = Tag("Event"), site = Tag("Site"), round = Tag("Round"), result = record.Result.ResultToken;
        // The same interpolation ChessVocabulary.PgnPlayingId performs, the line id included as C# formats it.
        string playingPre = $"chess/playing/{white}|{black}|{date}|{ev}|{round}|{site}|{record.LineId}|{result}";
        string eventPre = $"chess/event/{ev}|{site}|{date}";
        var playing = new IdentityView("PLAYING (occurrence)", Hex(record.PlayingId),
            "Hash128.OfCanonical: BLAKE3 of the UTF-8 string below (White|Black|Date|Event|Round|Site|LINE|Result)", playingPre,
            Hash128.OfCanonical(playingPre) == record.PlayingId, null, "minted from a formatted string, not composed from content (#46.1)");
        var eventView = new IdentityView("EVENT", Hex(record.EventId), "Hash128.OfCanonical: BLAKE3 of the UTF-8 string below (Event|Site|Date)",
            eventPre, Hash128.OfCanonical(eventPre) == record.EventId, null, "minted from a formatted string (#46.1)");
        var players = new List<PlayerView>();
        foreach (var (side, name) in new[] { ("White", white), ("Black", black) })
        {
            if (string.IsNullOrWhiteSpace(name) || name == "?") continue;
            string folded = PlayerAlias.Canonical(name);
            string pre = "chess/player/" + folded;
            players.Add(new PlayerView(side, name, folded, Hex(ChessVocabulary.PlayerId(name)), pre, Hash128.OfCanonical(pre) == ChessVocabulary.PlayerId(name)));
        }
        var line = LineView(record.PositionIds[0], record.MoveIds, record.LineId);

        // Each header's disposition by ablation through the same code: record the game again without that header and
        // see which identities change and which claims disappear.
        var headerViews = new List<HeaderView>();
        foreach (var (tag, value) in headers)
        {
            var d = new List<string>();
            string without = Regex.Replace(gameText, $"^\\[{Regex.Escape(tag)}\\s+\"(?:[^\"\\\\]|\\\\.)*\"\\]\\s*\\r?\\n", "", RegexOptions.Multiline);
            var ablated = ChessPgnDecomposer.TryParseGame(without) is { } r2 ? Record(r2, contentOf) : null;
            bool hashed = false, stored = false;
            if (ablated is null)
            {
                d.Add("identity: without it the game does not replay, so the trajectory starts there (P0)");
                hashed = true;
                if (tag is "SetUp" or "FEN" && full.ClaimKeys.FirstOrDefault(static k => k.Contains(" HAS_SETUP ", StringComparison.Ordinal)) is { } setup)
                    d.Add("stored: " + setup);
            }
            else
            {
                foreach (var (role, id) in full.Ids)
                    if (!ablated.Ids.TryGetValue(role, out var ablatedId) || ablatedId != id)
                    {
                        hashed = true;
                        d.Add(role == "LINE" ? "identity: part of the LINE (its start position P0)"
                            : role.StartsWith("player", StringComparison.Ordinal) ? $"hashed into {role} id" + (players.FirstOrDefault(p => role.Contains(p.Side, StringComparison.Ordinal)) is { } pv
                                ? $" as \"{pv.Folded}\"" + (pv.Folded == pv.NameAsWritten ? "" : " (folded)") : "")
                            : $"hashed into {role} id");
                    }
                foreach (var claim in full.ClaimKeys.Except(ablated.ClaimKeys))
                {
                    bool content = claim.Contains("content(", StringComparison.Ordinal);
                    d.Add((content ? "stored as content: " : "stored: ") + claim);
                    stored |= content;
                }
            }
            if (tag == "Result" && !hashed && !stored)
                d.Add($"the header is not read: the movetext's result token \"{result}\" is what is hashed into PLAYING and stored as HAS_RESULT");
            if (tag == "Variant") d.Add("read only to name a drop reason; never part of a position or the line");
            string summary = stored && hashed ? "content + hashed" : stored ? "content" : hashed ? "hashed only"
                : tag == "Result" && d.Count > 0 ? "not read" : "dropped";
            if (summary == "dropped") d.Add("not stored and not part of any identity");
            else if (summary == "hashed only") d.Add("not stored as content");
            headerViews.Add(new HeaderView(tag, value, summary, d));
        }

        var findings = new List<string>
        {
            $"#46.1 PLAYING is minted: OfCanonical(\"{Clip(playingPre, 200)}\"); EVENT likewise; players are OfCanonical(\"chess/player/\" + folded name)"
                + (players.Any(static p => p.Folded != p.NameAsWritten) ? $" ({string.Join(", ", players.Where(static p => p.Folded != p.NameAsWritten).Select(static p => $"\"{p.NameAsWritten}\" -> \"{p.Folded}\""))})" : "")
                + ". The law: a name is content as written; the occurrence is composed from content.",
            "#46.1 the PLAYING preimage interpolates the LINE id with C#'s record formatting (\"Hash128 { Hi = …, Lo = … }\"), not its hex: the identity depends on a .NET ToString.",
            $"#46.2 headers not stored as content: {string.Join(", ", headerViews.Where(static h => !h.Summary.StartsWith("content", StringComparison.Ordinal)).Select(static h => h.Tag + (h.Summary == "hashed only" ? " (hashed only)" : "")))}",
        };
        var (claims, other, entities) = (full.Claims, full.Other, full.Entities);
        findings.AddRange(CommonFindings(replay, line, variant, entities));
        if (comments.Count > 0 || nags > 0)
            findings.Add($"Comments ({comments.Count}) and glyphs ({nags}) are stored as content on PLAYING annotation trajectories; {variations} variation plies are not stored (main line only).");

        return new GameInspection(index, kinds, true, "recorded: LINE + PLAYING + EVENT + players + headers as RecordGame emits them",
            headerViews, Position(start), PlyViews(replay, walk.Mainline, all), Position(replay.Plies.Count > 0 ? replay.Plies[^1].After : start),
            line, playing, eventView, players, entities, claims, other, findings);
    }

    private sealed record Recorded(Dictionary<string, Hash128> Ids, HashSet<string> ClaimKeys, List<ClaimView> Claims,
        Dictionary<string, int> Other, List<EntityView> Entities);

    /// <summary>ChessPgnDecomposer.RecordGame into an unapplied change, read back with every id named by its role.</summary>
    private static Recorded Record(ChessGameRecord record, IReadOnlyDictionary<Hash128, string> contentOf)
    {
        using var builder = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "inspect");
        ChessPgnDecomposer.RecordGame(record, builder);
        var change = builder.Build();
        var ids = new Dictionary<string, Hash128> { ["LINE"] = record.LineId, ["PLAYING"] = record.PlayingId, ["EVENT"] = record.EventId };
        foreach (var (side, name) in new[] { ("White", record.WhiteName), ("Black", record.BlackName) })
            if (!string.IsNullOrWhiteSpace(name) && name != "?") ids[$"player({side})"] = ChessVocabulary.PlayerId(name);
        var roles = new Dictionary<Hash128, string>();
        foreach (var (role, id) in ids) roles.TryAdd(id, role);
        roles.TryAdd(record.PositionIds[0], "P0");
        for (int i = 1; i < record.PositionIds.Length; i++) roles.TryAdd(record.PositionIds[i], $"P{i}");
        for (int i = 0; i < record.MoveIds.Length; i++) roles.TryAdd(record.MoveIds[i], $"M{i + 1}");
        string Name(Hash128 id) => roles.TryGetValue(id, out var r) ? r : contentOf.TryGetValue(id, out var c) ? $"content(\"{Clip(c)}\")" : Hex(id)[..12];

        var relations = RelationNames();
        var claims = new List<ClaimView>();
        var other = new Dictionary<string, int>(StringComparer.Ordinal);
        var keySubjects = ids.Values.ToHashSet();
        foreach (var a in change.Attestations)
        {
            string rel = relations.TryGetValue(a.TypeId, out var rn) ? rn : Hex(a.TypeId)[..12];
            string? q = a.QualifierMask.IsZero ? null
                : !(a.QualifierMask & ChessVocabulary.WhiteSide).IsZero ? "side/white"
                : !(a.QualifierMask & ChessVocabulary.BlackSide).IsZero ? "side/black" : "qualified";
            if (keySubjects.Contains(a.SubjectId))
                claims.Add(new ClaimView(Name(a.SubjectId), rel, a.ObjectId is { } o ? Name(o) : null, a.ContextId is { } c ? Name(c) : null, q));
            else
                other[rel] = other.GetValueOrDefault(rel) + 1;
        }
        var keys = claims.Select(static c => $"[{c.Subject} {c.Relation}{(c.Qualifier is null ? "" : " " + c.Qualifier)} {c.Object ?? "-"}]"
            + (c.Context is null ? "" : $" in {c.Context}")).ToHashSet(StringComparer.Ordinal);
        var types = EntityTypeNames();
        var entities = change.Entities
            .Where(e => roles.TryGetValue(e.Id, out var r) && (keySubjects.Contains(e.Id) || r is "P0" or "M1"))
            .Select(e => new EntityView(Hex(e.Id), roles[e.Id], types.TryGetValue(e.TypeId, out var t) ? t
                : relations.TryGetValue(e.TypeId, out var rt) ? $"{rt} (a relation type id used as the entity type)" : Hex(e.TypeId)[..12], e.Tier))
            .DistinctBy(static e => (e.Id, e.Type)).ToList();
        return new Recorded(ids, keys, claims, other, entities);
    }

    private static List<string> CommonFindings(Replay replay, IdentityView line, string? variant, IReadOnlyList<EntityView> entities)
    {
        var f = new List<string>
        {
            $"#46.3 composition hashing: LINE {line.Id} = BLAKE3(0x01 ‖ children); without the domain byte (the law) it would be {line.LawId}.",
            "#46.4 floor: every position and move atom is a run of private bytes over ByteAtoms (0x80+domain, then 0xA0|high and 0xB0|low nibbles), not codepoints and digit compositions.",
        };
        var p0 = Position(replay.Start);
        f.Add($"#46.5 castling at P0: {p0.Castling}" + (p0.RookOverride is null ? "; no rook-file override atom" : $"; plus an override atom: {p0.RookOverride}") + ".");
        f.Add("#46.6 rules: " + p0.Rules + (string.IsNullOrEmpty(variant) ? "." : $"; the Variant \"{variant}\" governs legality only through the FEN's castling rights."));
        if (replay.Plies.Count == 0)
        {
            string typed = entities.FirstOrDefault(static e => e.Role == "LINE")?.Type ?? "Chess_Game";
            f.Add($"#46.7 empty game: the line collapses to its start position P0 ({line.Id}), which the recorder then types {typed}.");
        }
        return f;
    }

    private static string Clip(string s, int n = 60) => s.Length <= n ? s : s[..n] + "…";

    private static Dictionary<Hash128, string>? _relations;
    private static Dictionary<Hash128, string> RelationNames()
    {
        if (_relations is not null) return _relations;
        var map = new Dictionary<Hash128, string>();
        foreach (var r in RelationTypeRegistry.AllCanonical()) map.TryAdd(r.Id, r.Canonical);
        // surfaces the chess recorder writes that the live manifest lists only through a successor
        foreach (var name in new[] { "HAS_SETUP", "MOVE", "PLAYS_LINE", "HAS_EVENT", "GAME_HAS_ECO", "GAME_HAS_OPENING", "HAS_RATING", "OUTCOME" })
            try { map.TryAdd(RelationTypeRegistry.RelationTypeId(name), name); }
            catch (ArgumentException) { }
        return _relations = map;
    }

    private static Dictionary<Hash128, string>? _entityTypes;
    private static Dictionary<Hash128, string> EntityTypeNames()
    {
        if (_entityTypes is not null) return _entityTypes;
        var map = new Dictionary<Hash128, string>();
        if (LaplaceInstall.TryRepoRoot(out var root) && Path.Combine(root, "engine", "manifest", "entity_types.toml") is var toml && File.Exists(toml))
            foreach (Match m in Regex.Matches(File.ReadAllText(toml), "^canonical\\s*=\\s*\"([^\"]+)\"", RegexOptions.Multiline))
                try { map.TryAdd(EntityTypeRegistry.Id(m.Groups[1].Value), m.Groups[1].Value); } catch (ArgumentException) { }
        return _entityTypes = map;
    }

    // ---- text ----

    public static string Render(InspectReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"inspect {report.Kind}: {report.Input}");
        sb.AppendLine(report.Statement);
        foreach (var g in report.Games)
        {
            sb.AppendLine();
            sb.AppendLine($"== game {g.Index}: {string.Join("; ", g.RecordKinds)}");
            sb.AppendLine($"   {g.Disposition}");
            if (g.Headers.Count > 0)
            {
                sb.AppendLine("-- headers");
                int w = g.Headers.Max(static h => h.Tag.Length);
                foreach (var h in g.Headers)
                    sb.AppendLine($"   {h.Tag.PadRight(w)}  {Clip(h.Value, 50),-52} {h.Summary}: {string.Join("; ", h.Dispositions)}");
            }
            sb.AppendLine("-- start position P0");
            RenderPosition(sb, g.Start, full: true);
            sb.AppendLine($"-- transitions ({g.Plies.Count} plies): ply SAN uci  move id  -> position id   [transition key]");
            foreach (var p in g.Plies)
            {
                sb.AppendLine($"   {p.Ply,3} {p.San,-7} {p.Uci,-5}  M {p.MoveId[..16]}  -> P {p.To[..16]}  [{p.TransitionKey[..12]}]"
                    + (p.Comment is null ? "" : $"  {{{Clip(p.Comment, 30)}}}") + (p.Annotation is null ? "" : $"  {p.Annotation}"));
                if (p.Ply == 1)
                    sb.AppendLine("       M1 atoms: " + string.Join(", ", p.MoveAtoms.Select(static a => $"{a.Domain}={a.Meaning} [{a.Bytes}]")));
                if (p.Position is not null) RenderPosition(sb, p.Position, full: true);
            }
            if (g.Plies.Count > 0)
            {
                sb.AppendLine("-- final position");
                RenderPosition(sb, g.Final, full: false);
            }
            sb.AppendLine("-- identities");
            foreach (var id in new[] { g.Line, g.Playing, g.Event }.Where(static i => i is not null))
            {
                sb.AppendLine($"   {id!.Kind}: {id.Id}" + (id.Verified is null ? "" : id.Verified.Value ? "  (recomputed: matches)" : "  (recomputed: DIFFERS)"));
                sb.AppendLine($"      hashed from: {id.HashedFrom}");
                if (id.Preimage is not null) sb.AppendLine($"      preimage:    \"{id.Preimage}\"");
                if (id.LawId is not null) sb.AppendLine($"      law (no domain byte): {id.LawId}");
                if (id.Note is not null) sb.AppendLine($"      {id.Note}");
            }
            foreach (var p in g.Players)
                sb.AppendLine($"   PLAYER {p.Side}: {p.Id}  \"{p.NameAsWritten}\" -> OfCanonical(\"{p.Preimage}\")" + (p.Verified ? "" : "  (recomputed: DIFFERS)"));
            if (g.Entities.Count > 0)
            {
                sb.AppendLine("-- entity rows (key ids)");
                foreach (var e in g.Entities) sb.AppendLine($"   {e.Role,-14} {e.Id[..16]}  type {e.Type}  tier {e.Tier}");
            }
            if (g.Claims.Count > 0)
            {
                sb.AppendLine("-- claims on the line, occurrence, event and players");
                foreach (var c in g.Claims)
                    sb.AppendLine($"   [{c.Subject} {c.Relation}{(c.Qualifier is null ? "" : " " + c.Qualifier)} {c.Object ?? "-"}]" + (c.Context is null ? "" : $"  in {c.Context}"));
                if (g.OtherClaims.Count > 0)
                    sb.AppendLine("   other claims (per position/move): " + string.Join(", ", g.OtherClaims.Select(static o => $"{o.Key} x{o.Value}")));
            }
            sb.AppendLine("-- findings");
            foreach (var f in g.Findings) sb.AppendLine("   " + f);
        }
        return sb.ToString().TrimEnd();
    }

    private static void RenderPosition(StringBuilder sb, PositionView p, bool full)
    {
        sb.AppendLine($"   id {p.Id}   law {p.LawId}");
        sb.AppendLine($"   FEN (interchange only, never stored) {p.Fen}");
        sb.AppendLine($"   side: {p.SideToMove}; castling: {p.Castling}" + (p.RookOverride is null ? "" : $"; override: {p.RookOverride}")
            + $"; en passant: {p.EnPassant}; {p.Rules}; {p.PieceSquares} piece-square atoms");
        if (!full) return;
        foreach (var a in p.Atoms.Where(static a => a.Domain != "piece-square"))
            sb.AppendLine($"     {a.Domain,-22} {a.Value,5}  {a.Meaning,-40} bytes [{a.Bytes}]  atom {a.Id[..16]}");
        var ps = p.Atoms.Where(static a => a.Domain == "piece-square").ToList();
        if (ps.Count > 0)
            sb.AppendLine($"     piece-square x{ps.Count}: {string.Join(' ', ps.Select(static a => a.Meaning))}  (e.g. {ps[0].Meaning} bytes [{ps[0].Bytes}])");
    }
}
