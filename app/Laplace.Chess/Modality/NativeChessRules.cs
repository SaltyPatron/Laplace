using System.Globalization;
using System.Text;

namespace Laplace.Modality.Chess;

/// <summary>
/// Pure managed execution of chess's laws matching <c>Laplace-Engine/src/chess/chess_rules.c</c> and
/// <c>firmware/chess.firmware</c> (Standard, Chess960, Crazyhouse, King of the Hill, Three-check, Atomic,
/// Antichess, Horde, Racing Kings, and any custom axis declaration in <c>chess.firmware</c>).
/// </summary>
internal static class NativeChessRules
{
    private sealed class LawDef
    {
        public required string Name;
        public bool Castles = true;
        public bool C960;
        public bool Drops;
        public bool Explodes;
        public bool Forced;
        public bool Unroyal;
        public bool KingPromotes;
        public bool FirstDouble;
        public bool NoCheck;
        public int Checks;
        public bool GoalAnswered;
        public ulong Goal;
        public string Start = "";
    }

    private sealed class FirmwareCatalog
    {
        public string DefaultLaw = "Standard";
        public readonly List<LawDef> Laws = [];
        public readonly List<(string Name, LawDef Law)> Names = [];
        public readonly Dictionary<string, LawDef> ByName = new(StringComparer.Ordinal);
    }

    private struct Bd
    {
        public sbyte[] Sq;
        public byte Side;
        public byte Rights0, Rights1;
        public sbyte Ep;
        public bool C960;
        public ushort[] Hand0, Hand1;
        public uint Checks0, Checks1;
        public ulong Promoted;
        public LawDef? Law;
    }

    private readonly record struct Mv(int From, int To, int Promo, bool Castle, bool Ep, int Drop);

    private static readonly (int Df, int Dr)[] KnightOffsets = [(1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2)];
    private static readonly (int Df, int Dr)[] KingOffsets = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];
    private static readonly (int Df, int Dr)[] DiagOffsets = [(1, 1), (1, -1), (-1, 1), (-1, -1)];
    private static readonly (int Df, int Dr)[] OrthOffsets = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private const string PieceLetters = ".PNBRQK";

    private static readonly Lazy<FirmwareCatalog> Catalog = new(LoadCatalog, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string? LawCanonical(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return Catalog.Value.ByName.TryGetValue(name, out var law) ? law.Name : null;
    }

    internal static IReadOnlyList<string> LawNames()
        => Catalog.Value.Names.Select(static pair => pair.Name).ToArray();

    internal static bool LawKnown(string name)
        => !string.IsNullOrEmpty(name) && Catalog.Value.ByName.ContainsKey(name);

    internal static string? LawStart(string name)
    {
        if (!Catalog.Value.ByName.TryGetValue(name, out var law))
            throw new InvalidDataException($"The Engine's chess firmware declares no law {name}.");
        return string.IsNullOrEmpty(law.Start) ? null : law.Start;
    }

    internal static bool Supports(ChessVariantRules rules)
        => rules.Files == 8 && rules.Ranks == 8 && (rules.IsStandard || LawKnown(rules.Law));

    internal static int? Result(Board board)
    {
        var law = ResolveLaw(board.Rules);
        if (law is null) throw new InvalidDataException($"The Engine's chess firmware declares no law {board.Rules.Law}.");
        var b = Pack(board, law);
        if (Ended(in b))
        {
            if (law.Checks > 0 && b.Checks0 >= law.Checks) return 0;
            if (law.Checks > 0 && b.Checks1 >= law.Checks) return 1;
            if (law.Explodes && KingSq(in b, 0) < 0) return 1;
            if (law.Explodes && KingSq(in b, 1) < 0) return 0;
            bool g0 = OnGoal(in b, 0), g1 = OnGoal(in b, 1);
            if (g0 && g1) return -1;
            return g1 ? 1 : 0;
        }
        var legal = Legal(in b);
        if (legal.Count > 0) return null;
        if (law.Unroyal) return b.Side == 1 ? 1 : 0;
        if (PiecesOf(in b, b.Side) == 0) return b.Side == 1 ? 0 : 1;
        if (InCheck(in b, b.Side)) return b.Side == 1 ? 0 : 1;
        return -1;
    }

    internal static void Moves(Board board, List<ChessMove> output, bool legal, int? from = null)
    {
        var law = ResolveLaw(board.Rules);
        if (law is null) throw new InvalidDataException($"The Engine's chess firmware declares no law {board.Rules.Law}.");
        var b = Pack(board, law);
        var list = Ended(in b) ? [] : (legal ? Legal(in b) : Pseudo(in b));
        output.Clear();
        bool white = board.WhiteToMove;
        foreach (var mv in list)
        {
            var managed = ToManaged(mv, white);
            if (from is null || managed.From == from)
                output.Add(managed);
        }
    }

    internal static ChessMove? Resolve(Board board, string san)
    {
        var law = ResolveLaw(board.Rules);
        if (law is null || string.IsNullOrWhiteSpace(san)) return null;
        var b = Pack(board, law);
        if (Ended(in b)) return null;
        var all = Legal(in b);
        int idx = SanMatch(in b, san.Trim(), all);
        return idx < 0 ? null : ToManaged(all[idx], board.WhiteToMove);
    }

    internal static void Apply(Board board, ChessMove move)
    {
        var law = ResolveLaw(board.Rules)
            ?? throw new InvalidDataException($"The Engine's chess firmware declares no law {board.Rules.Law}.");
        var b = Pack(board, law);
        if (Ended(in b))
            throw new InvalidDataException($"Illegal move {move} for the declared chess rules.");
        var target = ToNative(board, move);
        var legal = Legal(in b);
        int found = -1;
        for (int i = 0; i < legal.Count; i++)
        {
            if (legal[i] == target) { found = i; break; }
        }
        if (found < 0)
            throw new InvalidDataException($"Illegal move {move} for the declared chess rules.");

        bool beforeWhite = board.WhiteToMove;
        bool isPawn = !move.IsDrop && !move.IsCastle && Board.TypeOf(board.Squares[move.From]) == Piece.WPawn;
        bool isCapture = !move.IsDrop && !move.IsCastle && (board.Squares[move.To] != Piece.Empty || (move.Flags & MoveFlags.EnPassant) != 0);

        Make(ref b, legal[found]);
        Unpack(in b, board);
        board.HalfmoveClock = (!move.IsDrop && (isPawn || isCapture)) ? 0 : board.HalfmoveClock + 1;
        if (!beforeWhite) board.FullmoveNumber++;
    }

    internal static (Board[] Boards, ChessMove[] Moves) Replay(Board initial, IReadOnlyList<string> sans)
    {
        var boards = new Board[sans.Count + 1];
        var moves = new ChessMove[sans.Count];
        boards[0] = initial.Clone();
        var cur = initial.Clone();
        for (int i = 0; i < sans.Count; i++)
        {
            var mv = Resolve(cur, sans[i]) ?? throw new InvalidDataException(
                $"illegal move at ply {i + 1}: '{sans[i]}' under {initial.Rules}");
            moves[i] = mv;
            Apply(cur, mv);
            boards[i + 1] = cur.Clone();
        }
        return (boards, moves);
    }

    private static LawDef? ResolveLaw(ChessVariantRules rules)
    {
        if (!Supports(rules)) return null;
        if (rules.IsStandard) return Catalog.Value.ByName["Standard"];
        if (!string.IsNullOrEmpty(rules.Law) && Catalog.Value.ByName.TryGetValue(rules.Law, out var named))
            return named;
        return new LawDef
        {
            Name = rules.Law,
            Castles = rules.Castling,
            Drops = rules.Drops,
            Explodes = rules.CaptureExplodes,
            Forced = rules.CaptureCompulsory,
            Unroyal = rules.BareKingWins,
            KingPromotes = rules.PromotionPieces.Contains('K', StringComparison.OrdinalIgnoreCase),
            FirstDouble = rules.PawnsDoubleFromFirstRank,
            NoCheck = rules.CheckForbidden,
            Checks = rules.WinByCheckCount,
            Goal = ParseGoalSquares(rules.WinBySquares),
            GoalAnswered = string.Equals(rules.Law, "Racing Kings", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static ulong ParseGoalSquares(string squares)
    {
        if (string.IsNullOrWhiteSpace(squares)) return 0;
        ulong mask = 0;
        foreach (var part in squares.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length == 2 && part[0] is >= 'a' and <= 'h' && part[1] is >= '1' and <= '8')
                mask |= 1UL << ((part[0] - 'a') + 8 * (part[1] - '1'));
        }
        return mask;
    }

    private static FirmwareCatalog LoadCatalog()
    {
        var cat = new FirmwareCatalog();
        string path = ResolveFirmwarePath();
        if (!File.Exists(path))
        {
            var std = new LawDef { Name = "Standard", Castles = true };
            var c960 = new LawDef { Name = "Chess960", Castles = true, C960 = true };
            AddLaw(cat, std, []);
            AddLaw(cat, c960, []);
            return cat;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            int hash = rawLine.IndexOf('#');
            string line = hash >= 0 ? rawLine[..hash] : rawLine;
            var tokens = TokenizeLine(line);
            if (tokens.Count == 0) continue;

            if (tokens[0] == "default")
            {
                if (tokens.Count > 1) cat.DefaultLaw = tokens[1];
                continue;
            }
            if (tokens[0] == "search" || tokens[0] != "variant" || tokens.Count < 2)
                continue;

            var law = new LawDef { Name = tokens[1], Castles = true };
            var aliases = new List<string>();
            bool bad = false;
            for (int i = 2; i < tokens.Count && !bad;)
            {
                string key = tokens[i++];
                switch (key)
                {
                    case "castling":
                        if (i >= tokens.Count) { bad = true; break; }
                        switch (tokens[i++])
                        {
                            case "standard": law.Castles = true; law.C960 = false; break;
                            case "chess960": law.Castles = true; law.C960 = true; break;
                            case "none": law.Castles = false; break;
                            default: bad = true; break;
                        }
                        break;
                    case "drops": law.Drops = true; break;
                    case "explodes": law.Explodes = true; break;
                    case "captures":
                        if (i >= tokens.Count || tokens[i++] != "forced") bad = true;
                        else law.Forced = true;
                        break;
                    case "king":
                        if (i >= tokens.Count) { bad = true; break; }
                        switch (tokens[i++])
                        {
                            case "unroyal": law.Unroyal = true; break;
                            case "promotes": law.KingPromotes = true; break;
                            default: bad = true; break;
                        }
                        break;
                    case "pawns":
                        if (i + 1 >= tokens.Count || tokens[i++] != "double" || tokens[i++] != "first") bad = true;
                        else law.FirstDouble = true;
                        break;
                    case "check":
                        if (i >= tokens.Count || tokens[i++] != "forbidden") bad = true;
                        else law.NoCheck = true;
                        break;
                    case "checks":
                        if (i >= tokens.Count || !int.TryParse(tokens[i++], NumberStyles.None, CultureInfo.InvariantCulture, out int k) || k is <= 0 or > 255) bad = true;
                        else law.Checks = k;
                        break;
                    case "goal":
                        bool any = false;
                        while (i < tokens.Count)
                        {
                            if (tokens[i] == "answered") { law.GoalAnswered = true; i++; continue; }
                            if (tokens[i].Length == 2 && tokens[i][0] is >= 'a' and <= 'h' && tokens[i][1] is >= '1' and <= '8')
                            {
                                law.Goal |= 1UL << ((tokens[i][0] - 'a') + 8 * (tokens[i][1] - '1'));
                                any = true;
                                i++;
                                continue;
                            }
                            break;
                        }
                        if (!any) bad = true;
                        break;
                    case "start":
                        if (i >= tokens.Count) bad = true;
                        else law.Start = tokens[i++];
                        break;
                    case "also":
                        while (i < tokens.Count && aliases.Count < 16) aliases.Add(tokens[i++]);
                        if (aliases.Count == 0) bad = true;
                        break;
                    default:
                        bad = true;
                        break;
                }
            }
            if (!bad) AddLaw(cat, law, aliases);
        }

        if (!cat.ByName.ContainsKey("Standard"))
            AddLaw(cat, new LawDef { Name = "Standard", Castles = true }, []);
        return cat;
    }

    private static void AddLaw(FirmwareCatalog cat, LawDef law, List<string> aliases)
    {
        if (cat.ByName.ContainsKey(law.Name)) return;
        cat.Laws.Add(law);
        cat.ByName[law.Name] = law;
        cat.Names.Add((law.Name, law));
        foreach (var alias in aliases)
        {
            if (cat.ByName.TryAdd(alias, law))
                cat.Names.Add((alias, law));
        }
    }

    private static List<string> TokenizeLine(string line)
    {
        var list = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t' || line[i] == '\r' || line[i] == '\n')) i++;
            if (i >= line.Length) break;
            if (line[i] == '"')
            {
                i++;
                int end = line.IndexOf('"', i);
                if (end < 0) break;
                list.Add(line[i..end]);
                i = end + 1;
            }
            else
            {
                int start = i;
                while (i < line.Length && line[i] != ' ' && line[i] != '\t' && line[i] != '\r' && line[i] != '\n') i++;
                list.Add(line[start..i]);
            }
        }
        return list;
    }

    private static string ResolveFirmwarePath()
    {
        static bool TryFile(string? candidate, out string resolved)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                string full = Path.GetFullPath(candidate.Trim());
                if (File.Exists(full)) { resolved = full; return true; }
            }
            resolved = "";
            return false;
        }

        if (TryFile(Environment.GetEnvironmentVariable("LAPLACE_CHESS_FIRMWARE"), out var configured))
            return configured;
        if (Environment.GetEnvironmentVariable("LAPLACE_FIRMWARE") is { Length: > 0 } programFirmware
            && Path.GetDirectoryName(Path.GetFullPath(programFirmware.Trim())) is { } fwDir
            && TryFile(Path.Combine(fwDir, "chess.firmware"), out var besideProgram))
            return besideProgram;
        if (Environment.GetEnvironmentVariable("LAPLACE_RECIPES") is { Length: > 0 } recipes
            && Directory.GetParent(Path.GetFullPath(recipes.Trim()))?.FullName is { } recParent
            && TryFile(Path.Combine(recParent, "firmware", "chess.firmware"), out var besideRecipes))
            return besideRecipes;
        if (TryFile(Path.Combine(AppContext.BaseDirectory, "native", "recipe-engine", "share", "laplace", "firmware", "chess.firmware"), out var bundled))
            return bundled;
        if (TryFile(Path.Combine(AppContext.BaseDirectory, "firmware", "chess.firmware"), out var local))
            return local;
        if (Laplace.Engine.Core.LaplaceInstall.TryRepoRoot(out var repoRoot)
            && Directory.GetParent(repoRoot)?.FullName is { } reposParent
            && TryFile(Path.Combine(reposParent, "Laplace-Engine", "firmware", "chess.firmware"), out var engineFirmware))
            return engineFirmware;

        return Path.Combine(AppContext.BaseDirectory, "firmware", "chess.firmware");
    }

    private static Bd Pack(Board board, LawDef law)
    {
        var sq = new sbyte[64];
        for (int r = 0; r < 8; r++)
            for (int f = 0; f < 8; f++)
                sq[r * 8 + f] = (sbyte)board.Squares[Board.Sq(f, r)];

        byte r0 = 0, r1 = 0;
        if ((board.Castle & CastleRights.WhiteKing) != 0) r0 |= (byte)(1 << board.WhiteKingRookFile);
        if ((board.Castle & CastleRights.WhiteQueen) != 0) r0 |= (byte)(1 << board.WhiteQueenRookFile);
        if ((board.Castle & CastleRights.BlackKing) != 0) r1 |= (byte)(1 << board.BlackKingRookFile);
        if ((board.Castle & CastleRights.BlackQueen) != 0) r1 |= (byte)(1 << board.BlackQueenRookFile);

        var h0 = new ushort[7];
        var h1 = new ushort[7];
        for (int p = 1; p <= 5; p++)
        {
            h0[p] = board.Pockets[p - 1];
            h1[p] = board.Pockets[5 + p - 1];
        }

        return new Bd
        {
            Sq = sq,
            Side = board.WhiteToMove ? (byte)0 : (byte)1,
            Rights0 = r0,
            Rights1 = r1,
            Ep = board.EpSquare < 0 ? (sbyte)-1 : (sbyte)(Board.FileOf(board.EpSquare) + 8 * Board.RankOf(board.EpSquare)),
            C960 = law.C960,
            Hand0 = h0,
            Hand1 = h1,
            Checks0 = board.Checks[0],
            Checks1 = board.Checks[1],
            Promoted = board.Promoted,
            Law = law,
        };
    }

    private static void Unpack(in Bd s, Board board)
    {
        for (int r = 0; r < 8; r++)
            for (int f = 0; f < 8; f++)
                board.Set(Board.Sq(f, r), (Piece)s.Sq[r * 8 + f]);

        for (int p = 1; p <= 5; p++)
        {
            board.Pockets[p - 1] = s.Hand0[p];
            board.Pockets[5 + p - 1] = s.Hand1[p];
        }
        board.Promoted = s.Promoted;
        board.Checks[0] = s.Checks0;
        board.Checks[1] = s.Checks1;
        board.WhiteToMove = s.Side == 0;
        board.EpSquare = s.Ep < 0 ? -1 : Board.Sq(s.Ep & 7, s.Ep >> 3);

        board.Castle = CastleRights.None;
        board.WhiteKingRookFile = 7;
        board.WhiteQueenRookFile = 0;
        board.BlackKingRookFile = 7;
        board.BlackQueenRookFile = 0;

        int wKing = KingSq(in s, 0);
        if (wKing >= 0 && s.Rights0 != 0)
        {
            int kf = wKing & 7;
            for (int f = 0; f < 8; f++)
            {
                if ((s.Rights0 & (1 << f)) == 0) continue;
                if (f > kf) { board.Castle |= CastleRights.WhiteKing; board.WhiteKingRookFile = (sbyte)f; }
                else { board.Castle |= CastleRights.WhiteQueen; board.WhiteQueenRookFile = (sbyte)f; }
            }
        }
        int bKing = KingSq(in s, 1);
        if (bKing >= 0 && s.Rights1 != 0)
        {
            int kf = bKing & 7;
            for (int f = 0; f < 8; f++)
            {
                if ((s.Rights1 & (1 << f)) == 0) continue;
                if (f > kf) { board.Castle |= CastleRights.BlackKing; board.BlackKingRookFile = (sbyte)f; }
                else { board.Castle |= CastleRights.BlackQueen; board.BlackQueenRookFile = (sbyte)f; }
            }
        }
    }

    private static Bd Clone(in Bd s) => new()
    {
        Sq = (sbyte[])s.Sq.Clone(),
        Side = s.Side,
        Rights0 = s.Rights0,
        Rights1 = s.Rights1,
        Ep = s.Ep,
        C960 = s.C960,
        Hand0 = (ushort[])s.Hand0.Clone(),
        Hand1 = (ushort[])s.Hand1.Clone(),
        Checks0 = s.Checks0,
        Checks1 = s.Checks1,
        Promoted = s.Promoted,
        Law = s.Law,
    };

    private static ChessMove ToManaged(Mv m, bool white)
    {
        if (m.Drop != 0)
        {
            int sq = Board.Sq(m.To & 7, m.To >> 3);
            Piece promo = (Piece)(white ? m.Drop : -m.Drop);
            return new ChessMove(sq, sq, promo, MoveFlags.Drop);
        }
        if (m.Castle)
        {
            int rank = m.From >> 3;
            bool kingSide = (m.To & 7) > (m.From & 7);
            int from = Board.Sq(m.From & 7, rank);
            int to = Board.Sq(kingSide ? CastlePaths.KingSideKingFile : CastlePaths.QueenSideKingFile, rank);
            return new ChessMove(from, to, Piece.Empty, MoveFlags.Castle);
        }
        int from0x88 = Board.Sq(m.From & 7, m.From >> 3);
        int to0x88 = Board.Sq(m.To & 7, m.To >> 3);
        MoveFlags flags = MoveFlags.None;
        if (m.Ep) flags |= MoveFlags.EnPassant;
        if (m.Promo != 0) flags |= MoveFlags.Promotion;
        Piece promoPiece = m.Promo == 0 ? Piece.Empty : (Piece)(white ? m.Promo : -m.Promo);
        return new ChessMove(from0x88, to0x88, promoPiece, flags);
    }

    private static Mv ToNative(Board b, ChessMove move)
    {
        if (move.IsDrop)
        {
            int sq = (Board.RankOf(move.To) << 3) | Board.FileOf(move.To);
            return new Mv(sq, sq, 0, false, false, Math.Abs((sbyte)move.Promotion));
        }
        int from = (Board.RankOf(move.From) << 3) | Board.FileOf(move.From);
        if (move.IsCastle)
        {
            int backRank = Board.RankOf(move.From);
            int rookFile = b.CastleRookFile(b.WhiteToMove, move.IsKingSideCastle);
            return new Mv(from, backRank * 8 + rookFile, 0, true, false, 0);
        }
        int to = (Board.RankOf(move.To) << 3) | Board.FileOf(move.To);
        int promo = move.IsPromotion ? Math.Abs((sbyte)move.Promotion) : 0;
        return new Mv(from, to, promo, false, (move.Flags & MoveFlags.EnPassant) != 0, 0);
    }

    private static bool On(int f, int r) => (uint)f < 8 && (uint)r < 8;
    private static int ColourOf(sbyte p) => p > 0 ? 0 : 1;
    private static int KingSq(in Bd b, int c)
    {
        sbyte k = c == 0 ? (sbyte)6 : (sbyte)-6;
        for (int s = 0; s < 64; s++) if (b.Sq[s] == k) return s;
        return -1;
    }
    private static bool Adjacent(int a, int b) => Math.Abs((a & 7) - (b & 7)) <= 1 && Math.Abs((a >> 3) - (b >> 3)) <= 1;
    private static bool Attacked(in Bd b, int s, int by)
    {
        int f = s & 7, r = s >> 3, sg = by != 0 ? -1 : 1;
        int pr = r - (by != 0 ? -1 : 1);
        for (int df = -1; df <= 1; df += 2)
            if (On(f + df, pr) && b.Sq[pr * 8 + f + df] == sg * 1) return true;
        foreach (var (df, dr) in KnightOffsets)
            if (On(f + df, r + dr) && b.Sq[(r + dr) * 8 + f + df] == sg * 2) return true;
        foreach (var (df, dr) in KingOffsets)
            if (On(f + df, r + dr) && b.Sq[(r + dr) * 8 + f + df] == sg * 6) return true;
        foreach (var (df, dr) in DiagOffsets)
        {
            for (int ff = f + df, rr = r + dr; On(ff, rr); ff += df, rr += dr)
            {
                sbyte p = b.Sq[rr * 8 + ff];
                if (p == 0) continue;
                if (p == sg * 3 || p == sg * 5) return true;
                break;
            }
        }
        foreach (var (df, dr) in OrthOffsets)
        {
            for (int ff = f + df, rr = r + dr; On(ff, rr); ff += df, rr += dr)
            {
                sbyte p = b.Sq[rr * 8 + ff];
                if (p == 0) continue;
                if (p == sg * 4 || p == sg * 5) return true;
                break;
            }
        }
        return false;
    }

    private static bool InCheck(in Bd b, int c)
    {
        var w = b.Law;
        if (w is { Unroyal: true }) return false;
        int k = KingSq(in b, c);
        if (k < 0) return false;
        if (w is { Explodes: true })
        {
            int o = KingSq(in b, c ^ 1);
            if (o >= 0 && Adjacent(k, o)) return false;
        }
        return Attacked(in b, k, c ^ 1);
    }

    private static void RightsHeld(ref Bd b)
    {
        for (int c = 0; c < 2; c++)
        {
            byte rights = c == 0 ? b.Rights0 : b.Rights1;
            if (rights == 0) continue;
            int back = c != 0 ? 56 : 0, ks = KingSq(in b, c);
            sbyte rk = (sbyte)(c != 0 ? -4 : 4);
            if (ks < 0 || (ks & ~7) != back) rights = 0;
            else
            {
                for (int f = 0; f < 8; f++)
                    if ((rights & (1 << f)) != 0 && b.Sq[back + f] != rk) rights &= (byte)~(1 << f);
            }
            if (c == 0) b.Rights0 = rights;
            else b.Rights1 = rights;
        }
    }

    private static void Make(ref Bd b, Mv m)
    {
        var w = b.Law;
        int c = b.Side, back = c != 0 ? 56 : 0;
        if (m.Drop != 0)
        {
            b.Sq[m.To] = (sbyte)(c != 0 ? -m.Drop : m.Drop);
            var hand = c == 0 ? b.Hand0 : b.Hand1;
            if (hand[m.Drop] > 0) hand[m.Drop]--;
            b.Promoted &= ~(1UL << m.To);
            b.Ep = -1;
            b.Side = (byte)(c ^ 1);
        }
        else if (m.Castle)
        {
            int ks = m.From, rs = m.To;
            bool kingSide = (rs & 7) > (ks & 7);
            sbyte k = b.Sq[ks], rk = b.Sq[rs];
            b.Sq[ks] = 0;
            b.Sq[rs] = 0;
            b.Sq[back + (kingSide ? 6 : 2)] = k;
            b.Sq[back + (kingSide ? 5 : 3)] = rk;
            b.Promoted &= ~((1UL << ks) | (1UL << rs));
            if (c == 0) b.Rights0 = 0;
            else b.Rights1 = 0;
            b.Ep = -1;
            b.Side = (byte)(c ^ 1);
        }
        else
        {
            sbyte p = b.Sq[m.From];
            int capsq = m.Ep ? ((m.From & ~7) | (m.To & 7)) : m.To;
            sbyte cap = b.Sq[capsq];
            if (cap != 0 && w is { Drops: true })
            {
                int kind = (b.Promoted & (1UL << capsq)) != 0 ? 1 : Math.Abs((int)cap);
                var hand = c == 0 ? b.Hand0 : b.Hand1;
                if (kind != 6 && hand[kind] < ushort.MaxValue) hand[kind]++;
            }
            bool promoted = (b.Promoted & (1UL << m.From)) != 0 || m.Promo != 0;
            b.Promoted &= ~((1UL << m.From) | (1UL << capsq) | (1UL << m.To));
            if ((p == 6 || p == -6) && (w is null || w.Castles))
            {
                if (c == 0) b.Rights0 = 0;
                else b.Rights1 = 0;
            }
            b.Sq[m.To] = m.Promo != 0 ? (sbyte)(c != 0 ? -m.Promo : m.Promo) : p;
            b.Sq[m.From] = 0;
            if (m.Ep) b.Sq[capsq] = 0;
            if (promoted && w is { Drops: true }) b.Promoted |= 1UL << m.To;
            if (cap != 0 && w is { Explodes: true })
            {
                int f = m.To & 7, r = m.To >> 3;
                b.Sq[m.To] = 0;
                foreach (var (df, dr) in KingOffsets)
                {
                    int ff = f + df, rr = r + dr;
                    if (!On(ff, rr)) continue;
                    int sq = rr * 8 + ff;
                    sbyte q = b.Sq[sq];
                    if (q != 0 && Math.Abs((int)q) != 1)
                    {
                        b.Sq[sq] = 0;
                        b.Promoted &= ~(1UL << sq);
                    }
                }
            }
            b.Ep = -1;
            if ((p == 1 || p == -1) && Math.Abs((m.To >> 3) - (m.From >> 3)) == 2)
                b.Ep = (sbyte)((m.From + m.To) / 2);
            b.Side = (byte)(c ^ 1);
        }
        RightsHeld(ref b);
        if (w is { Checks: > 0 } && InCheck(in b, b.Side))
        {
            if (c == 0) { if (b.Checks0 < 255) b.Checks0++; }
            else { if (b.Checks1 < 255) b.Checks1++; }
        }
    }

    private static bool CaptureOf(in Bd b, Mv m) => !m.Castle && m.Drop == 0 && (b.Sq[m.To] != 0 || m.Ep);
    private static bool OnGoal(in Bd b, int c)
    {
        int k = KingSq(in b, c);
        return k >= 0 && b.Law is { } w && (w.Goal & (1UL << k)) != 0;
    }
    private static int PiecesOf(in Bd b, int c)
    {
        int n = 0;
        for (int s = 0; s < 64; s++) if (b.Sq[s] != 0 && ColourOf(b.Sq[s]) == c) n++;
        var hand = c == 0 ? b.Hand0 : b.Hand1;
        for (int k = 1; k <= 5; k++) n += hand[k];
        return n;
    }

    private static List<Mv> Pseudo(in Bd b)
    {
        var w = b.Law;
        var ps = new List<Mv>(64);
        int c = b.Side, sg = c != 0 ? -1 : 1;
        int top = w is { KingPromotes: true } ? 6 : 5;

        for (int s = 0; s < 64; s++)
        {
            sbyte p = b.Sq[s];
            if (p == 0 || ColourOf(p) != c) continue;
            int a = Math.Abs((int)p), f = s & 7, r = s >> 3;
            if (a == 1)
            {
                int dr = c != 0 ? -1 : 1, last = c != 0 ? 0 : 7, start = c != 0 ? 6 : 1, first = c != 0 ? 7 : 0;
                if (On(f, r + dr) && b.Sq[(r + dr) * 8 + f] == 0)
                {
                    int to = (r + dr) * 8 + f;
                    if (r + dr == last) for (int pr = 2; pr <= top; pr++) ps.Add(new Mv(s, to, pr, false, false, 0));
                    else ps.Add(new Mv(s, to, 0, false, false, 0));

                    if ((r == start || (r == first && w is { FirstDouble: true })) && On(f, r + 2 * dr) && b.Sq[(r + 2 * dr) * 8 + f] == 0)
                        ps.Add(new Mv(s, (r + 2 * dr) * 8 + f, 0, false, false, 0));
                }
                for (int df = -1; df <= 1; df += 2)
                {
                    if (!On(f + df, r + dr)) continue;
                    int t = (r + dr) * 8 + f + df;
                    if (b.Sq[t] != 0 && ColourOf(b.Sq[t]) != c)
                    {
                        if (r + dr == last) for (int pr = 2; pr <= top; pr++) ps.Add(new Mv(s, t, pr, false, false, 0));
                        else ps.Add(new Mv(s, t, 0, false, false, 0));
                    }
                    else if (t == b.Ep && b.Sq[t] == 0 && b.Sq[r * 8 + f + df] == -sg * 1)
                        ps.Add(new Mv(s, t, 0, false, true, 0));
                }
            }
            else if (a == 2 || a == 6)
            {
                var deltas = a == 2 ? KnightOffsets : KingOffsets;
                foreach (var (df, dr) in deltas)
                {
                    int ff = f + df, rr = r + dr;
                    if (!On(ff, rr)) continue;
                    sbyte q = b.Sq[rr * 8 + ff];
                    if (q != 0 && ColourOf(q) == c) continue;
                    ps.Add(new Mv(s, rr * 8 + ff, 0, false, false, 0));
                }
            }
            else
            {
                for (int k = 0; k < 2; k++)
                {
                    if ((k == 0 && a == 4) || (k == 1 && a == 3)) continue;
                    var deltas = k != 0 ? OrthOffsets : DiagOffsets;
                    foreach (var (df, dr) in deltas)
                    {
                        for (int ff = f + df, rr = r + dr; On(ff, rr); ff += df, rr += dr)
                        {
                            sbyte q = b.Sq[rr * 8 + ff];
                            if (q != 0 && ColourOf(q) == c) break;
                            ps.Add(new Mv(s, rr * 8 + ff, 0, false, false, 0));
                            if (q != 0) break;
                        }
                    }
                }
            }
        }

        int back = c != 0 ? 56 : 0, ks = KingSq(in b, c);
        byte rights = c == 0 ? b.Rights0 : b.Rights1;
        if ((w is null || w.Castles) && ks >= 0 && (ks & ~7) == back && rights != 0 && !InCheck(in b, c))
        {
            for (int rf = 0; rf < 8; rf++)
            {
                if ((rights & (1 << rf)) == 0) continue;
                int rs = back + rf;
                if (b.Sq[rs] != sg * 4) continue;
                bool kingSide = rf > (ks & 7);
                int kd = back + (kingSide ? 6 : 2), rd = back + (kingSide ? 5 : 3);
                bool ok = true;
                int lo = Math.Min(ks, kd), hi = Math.Max(ks, kd);
                for (int s = lo; s <= hi && ok; s++) if (s != ks && s != rs && b.Sq[s] != 0) ok = false;
                int rlo = Math.Min(rs, rd), rhi = Math.Max(rs, rd);
                for (int s = rlo; s <= rhi && ok; s++) if (s != ks && s != rs && b.Sq[s] != 0) ok = false;
                for (int s = lo; s <= hi && ok; s++)
                {
                    var t = Clone(in b);
                    t.Sq[ks] = 0; t.Sq[rs] = 0; t.Sq[s] = (sbyte)(sg * 6);
                    if (InCheck(in t, c)) ok = false;
                }
                if (ok) ps.Add(new Mv(ks, rs, 0, true, false, 0));
            }
        }

        if (w is { Drops: true })
        {
            var hand = c == 0 ? b.Hand0 : b.Hand1;
            for (int kind = 1; kind <= 5; kind++)
            {
                if (hand[kind] == 0) continue;
                for (int s = 0; s < 64; s++)
                    if (b.Sq[s] == 0 && (kind != 1 || ((s >> 3) != 0 && (s >> 3) != 7)))
                        ps.Add(new Mv(s, s, 0, false, false, kind));
            }
        }

        return ps;
    }

    private static List<Mv> Legal(in Bd b)
    {
        var w = b.Law;
        var ps = Pseudo(in b);
        var res = new List<Mv>(ps.Count);
        int c = b.Side;
        foreach (var m in ps)
        {
            if (w is not { Unroyal: true })
            {
                var t = Clone(in b);
                Make(ref t, m);
                if (w is { Explodes: true })
                {
                    if (KingSq(in b, c) >= 0 && KingSq(in t, c) < 0) continue;
                    if (KingSq(in t, c ^ 1) >= 0 && InCheck(in t, c)) continue;
                }
                else if (InCheck(in t, c)) continue;
                if (w is { NoCheck: true } && InCheck(in t, c ^ 1)) continue;
            }
            res.Add(m);
        }

        if (w is { Forced: true })
        {
            bool anyCapture = false;
            for (int i = 0; i < res.Count; i++)
            {
                if (CaptureOf(in b, res[i])) { anyCapture = true; break; }
            }
            if (anyCapture)
            {
                int keep = 0;
                for (int i = 0; i < res.Count; i++)
                    if (CaptureOf(in b, res[i])) res[keep++] = res[i];
                res.RemoveRange(keep, res.Count - keep);
            }
        }

        return res;
    }

    private static bool Ended(in Bd b)
    {
        var w = b.Law;
        if (w is null) return false;
        if (w.Checks > 0 && (b.Checks0 >= w.Checks || b.Checks1 >= w.Checks)) return true;
        if (w.Explodes && ((KingSq(in b, 0) < 0) != (KingSq(in b, 1) < 0))) return true;
        if (w.Goal != 0)
        {
            bool g0 = OnGoal(in b, 0), g1 = OnGoal(in b, 1);
            if (!w.GoalAnswered) return g0 || g1;
            if (g1) return true;
            if (g0)
            {
                if (b.Side == 0) return true;
                foreach (var m in Legal(in b))
                    if (Math.Abs((int)b.Sq[m.From]) == 6 && m.Drop == 0 && (w.Goal & (1UL << m.To)) != 0)
                        return false;
                return true;
            }
        }
        return false;
    }

    private static string SanOf(in Bd b, Mv m, IReadOnlyList<Mv> all)
    {
        if (m.Drop != 0)
            return $"{PieceLetters[m.Drop]}@{(char)('a' + (m.To & 7))}{(char)('1' + (m.To >> 3))}";
        if (m.Castle)
            return (m.To & 7) > (m.From & 7) ? "O-O" : "O-O-O";

        var sb = new StringBuilder(8);
        int a = Math.Abs((int)b.Sq[m.From]);
        bool cap = b.Sq[m.To] != 0 || m.Ep;
        if (a == 1)
        {
            if (cap) sb.Append((char)('a' + (m.From & 7))).Append('x');
        }
        else
        {
            sb.Append(PieceLetters[a]);
            bool same = false, file = false, rank = false;
            foreach (var x in all)
            {
                if (x.Castle || x.Drop != 0 || x.To != m.To || x.From == m.From || Math.Abs((int)b.Sq[x.From]) != a) continue;
                same = true;
                if ((x.From & 7) == (m.From & 7)) file = true;
                if ((x.From >> 3) == (m.From >> 3)) rank = true;
            }
            if (same)
            {
                if (!file) sb.Append((char)('a' + (m.From & 7)));
                else if (!rank) sb.Append((char)('1' + (m.From >> 3)));
                else sb.Append((char)('a' + (m.From & 7))).Append((char)('1' + (m.From >> 3)));
            }
            if (cap) sb.Append('x');
        }
        sb.Append((char)('a' + (m.To & 7))).Append((char)('1' + (m.To >> 3)));
        if (m.Promo != 0) sb.Append('=').Append(PieceLetters[m.Promo]);
        return sb.ToString();
    }

    private static int SanMatch(in Bd b, string written, IReadOnlyList<Mv> all)
    {
        int n = written.Length;
        while (n > 0 && (written[n - 1] is '!' or '?')) n--;
        while (n > 0 && (written[n - 1] is '+' or '#')) n--;
        if (n == 0 || n > 31) return -1;

        string tok = written[..n];
        string t = tok.Contains('-') ? tok.Replace('0', 'O') : tok.Replace('O', '0');
        for (int i = 0; i < all.Count; i++)
            if (string.Equals(SanOf(in b, all[i], all), t, StringComparison.Ordinal)) return i;

        int at = t.IndexOf('@');
        if (at >= 0)
        {
            int kind = 1;
            if (at > 0)
            {
                if (at != 1 || t[0] == '.') return -1;
                kind = PieceLetters.IndexOf(t[0]);
                if (kind < 1) return -1;
            }
            if (t.Length - at - 1 != 2 || t[at + 1] is < 'a' or > 'h' || t[at + 2] is < '1' or > '8') return -1;
            int dropTo = (t[at + 1] - 'a') + 8 * (t[at + 2] - '1');
            for (int i = 0; i < all.Count; i++)
                if (all[i].Drop == kind && all[i].To == dropTo) return i;
            return -1;
        }

        int pc = 1, ff = -1, fr = -1, pr = 0, idx = 0;
        if (idx < t.Length && "NBRQK".Contains(t[idx])) { pc = PieceLetters.IndexOf(t[idx]); idx++; }
        Span<char> sq = stackalloc char[6];
        int ns = 0;
        for (; idx < t.Length && ns < 6; idx++)
        {
            char d = t[idx];
            if (d is >= 'a' and <= 'h' or >= '1' and <= '8') sq[ns++] = d;
            else if (d is '=' or 'x' or '-') continue;
            else if ("NBRQK".Contains(d)) { pr = PieceLetters.IndexOf(d); break; }
            else return -1;
        }
        if (ns < 2) return -1;
        int dest = (sq[ns - 2] - 'a') + 8 * (sq[ns - 1] - '1');
        if (dest is < 0 or > 63) return -1;
        for (int k = 0; k < ns - 2; k++)
        {
            if (sq[k] is >= 'a' and <= 'h') ff = sq[k] - 'a';
            else fr = sq[k] - '1';
        }
        int hit = -1;
        for (int i = 0; i < all.Count; i++)
        {
            var m = all[i];
            if (m.Castle || m.Drop != 0 || m.To != dest || Math.Abs((int)b.Sq[m.From]) != pc || m.Promo != pr) continue;
            if (ff >= 0 && (m.From & 7) != ff) continue;
            if (fr >= 0 && (m.From >> 3) != fr) continue;
            if (hit >= 0) return -1;
            hit = i;
        }
        return hit;
    }
}
