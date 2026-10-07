using System.Globalization;
using System.Text.RegularExpressions;

namespace Laplace.Chess.Service.Uci;

// The codec between standard UCI and Laplace: every engine's output decodes into one typed model, and every
// request encodes from it. Notation stays notation here (FEN, UCI moves): the content-addressed position and move
// identities belong to the native core, reached through IChessIdentityResolver, never minted from these strings.

/// <summary>A move in UCI long algebraic notation (e2e4, e7e8q). Notation only, never an identity.</summary>
public readonly partial record struct UciMove
{
    public string Text { get; }
    private UciMove(string text) => Text = text;

    public static UciMove Parse(string text)
        => TryParse(text, out var move) ? move : throw new FormatException($"'{text}' is not a UCI move.");

    public static bool TryParse(string? text, out UciMove move)
    {
        move = default;
        if (text is null || !Shape().IsMatch(text)) return false;
        move = new UciMove(text);
        return true;
    }

    public override string ToString() => Text;

    [GeneratedRegex("^[a-h][1-8][a-h][1-8][qrbn]?$")]
    private static partial Regex Shape();
}

/// <summary>A position as notation: a start FEN and the UCI moves played from it. <see cref="WhiteToMove"/> is read from
/// the FEN's side field and the move count, which is all the codec needs to orient a score; it never validates chess.</summary>
public sealed record ChessPosition(string Fen, IReadOnlyList<UciMove> Moves)
{
    public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    public static ChessPosition Start { get; } = new(StartFen, []);

    public static ChessPosition From(string? fen, IEnumerable<string>? moves = null)
    {
        string f = string.IsNullOrWhiteSpace(fen) || fen.Trim() == "startpos" ? StartFen : fen.Trim();
        if (f.Contains('\n') || f.Contains('\r') || f.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is < 2 or > 6)
            throw new FormatException("FEN must be one line with at least a board and a side to move.");
        var list = (moves ?? []).Where(static m => !string.IsNullOrWhiteSpace(m)).Select(static m => UciMove.Parse(m.Trim())).ToList();
        return new ChessPosition(f, list);
    }

    public bool WhiteToMove
    {
        get
        {
            bool white = Fen.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1] != "b";
            return Moves.Count % 2 == 0 ? white : !white;
        }
    }

    /// <summary>The UCI <c>position</c> command for this position.</summary>
    public string Encode()
    {
        string head = Fen == StartFen ? "position startpos" : "position fen " + Fen;
        return Moves.Count == 0 ? head : head + " moves " + string.Join(' ', Moves.Select(static m => m.Text));
    }
}

/// <summary>The seam where notation becomes Laplace identity. Today a pass-through that keeps notation as notation; the
/// native core will resolve it to the canonical content-addressed position and move IDs. Nothing here hashes a FEN.</summary>
public interface IChessIdentityResolver
{
    ChessPositionKey ResolvePosition(ChessPosition position);
}

/// <summary>A position as a reader keys it: the notation, and the content ID once a resolver can supply it (null now).</summary>
public sealed record ChessPositionKey(string Fen, IReadOnlyList<string> Moves, string? ContentId = null);

public sealed class NotationIdentityResolver : IChessIdentityResolver
{
    public static NotationIdentityResolver Instance { get; } = new();
    public ChessPositionKey ResolvePosition(ChessPosition position)
        => new(position.Fen, position.Moves.Select(static m => m.Text).ToList());
}

/// <summary>Search limits in UCI terms. Null is "not given". Clock values are what the engine is told, after the caller
/// has taken its own overhead off (<see cref="UciClock"/>).</summary>
public sealed record UciLimits(
    long? Nodes = null, int? Depth = null, long? MoveTimeMs = null,
    long? WTimeMs = null, long? BTimeMs = null, long? WIncMs = null, long? BIncMs = null,
    int? MovesToGo = null, bool Infinite = false)
{
    public bool Bounded => Nodes is > 0 || Depth is > 0 || MoveTimeMs is > 0 || WTimeMs is not null || BTimeMs is not null;

    public string Encode()
    {
        if (Infinite) return "go infinite";
        var parts = new List<string> { "go" };
        void Add(string key, long? value) { if (value is { } v) parts.Add(key + " " + v.ToString(CultureInfo.InvariantCulture)); }
        Add("wtime", WTimeMs); Add("btime", BTimeMs); Add("winc", WIncMs); Add("binc", BIncMs);
        Add("movestogo", MovesToGo); Add("depth", Depth); Add("nodes", Nodes); Add("movetime", MoveTimeMs);
        return string.Join(' ', parts);
    }

    /// <summary>Decode a <c>go</c> command. A missing, malformed or negative value is "not given".</summary>
    public static UciLimits Decode(IReadOnlyList<string> tokens)
    {
        long? Number(string key)
        {
            for (int i = 0; i + 1 < tokens.Count; i++)
                if (tokens[i] == key)
                    return long.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
            return null;
        }
        int? Int(string key) => Number(key) is { } v ? (int)Math.Min(v, int.MaxValue) : null;
        return new UciLimits(Number("nodes"), Int("depth"), Number("movetime"),
            Number("wtime"), Number("btime"), Number("winc"), Number("binc"), Int("movestogo"),
            tokens.Contains("infinite"));
    }
}

/// <summary>A game clock at the boundary: the remaining times as the server reports them and the overhead the caller
/// reserves for the network and its own work. The engine is told the remainder minus the overhead; the receipt keeps
/// both, so network time is never counted as engine time.</summary>
public sealed record UciClock(long WTimeMs, long BTimeMs, long WIncMs, long BIncMs, long OverheadMs = 0)
{
    public UciLimits ToLimits(int? depthCap = null) => new(
        Depth: depthCap,
        WTimeMs: Math.Max(1, WTimeMs - OverheadMs), BTimeMs: Math.Max(1, BTimeMs - OverheadMs),
        WIncMs: WIncMs, BIncMs: BIncMs);
}

public enum ScoreBound { Exact, Lower, Upper }

/// <summary>An engine score. UCI reports it from the side to move; <see cref="WhiteCp"/>/<see cref="WhiteMate"/> are the
/// same score from White's view. Exactly one of Cp and Mate is set.</summary>
public sealed record EngineScore(int? Cp, int? Mate, bool WhiteToMove, ScoreBound Bound = ScoreBound.Exact)
{
    public string Perspective => "side-to-move";
    public int? WhiteCp => Cp is { } cp ? (WhiteToMove ? cp : -cp) : null;
    public int? WhiteMate => Mate is { } m ? (WhiteToMove ? m : -m) : null;

    /// <summary>Side-to-move centipawns with mate mapped to the magnitude convention PgnEvals uses for "#N".</summary>
    public int SideToMoveCentipawns => Cp ?? (Mate is 0 or null ? -20_000
        : Math.Sign(Mate.Value) * (20_000 - Math.Min(Math.Abs(Mate.Value), 100) * 100));
}

public readonly record struct EngineWdl(int Win, int Draw, int Loss);

/// <summary>One decoded <c>info</c> line. Every field is what the engine said, or null when it did not say it.</summary>
public sealed record EngineInfo(
    int? Depth = null, int? SelDepth = null, int? MultiPv = null, EngineScore? Score = null, EngineWdl? Wdl = null,
    long? Nodes = null, long? Nps = null, long? TbHits = null, long? TimeMs = null, int? HashFull = null,
    IReadOnlyList<UciMove>? Pv = null, string? Text = null)
{
    /// <summary>Decode an engine's <c>info</c> line; null for any other line.</summary>
    public static EngineInfo? Decode(string line, bool whiteToMove)
    {
        if (!line.StartsWith("info", StringComparison.Ordinal) || (line.Length > 4 && line[4] != ' ')) return null;
        var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int? depth = null, sel = null, mpv = null, hashfull = null;
        long? nodes = null, nps = null, tb = null, time = null;
        EngineScore? score = null; EngineWdl? wdl = null; List<UciMove>? pv = null; string? text = null;
        static long? L(string[] t, int i) => i < t.Length && long.TryParse(t[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        static int? I(string[] t, int i) => L(t, i) is { } v ? (int)Math.Clamp(v, int.MinValue, int.MaxValue) : null;
        for (int i = 1; i < t.Length; i++)
        {
            switch (t[i])
            {
                case "depth": depth = I(t, ++i); break;
                case "seldepth": sel = I(t, ++i); break;
                case "multipv": mpv = I(t, ++i); break;
                case "nodes": nodes = L(t, ++i); break;
                case "nps": nps = L(t, ++i); break;
                case "tbhits": tb = L(t, ++i); break;
                case "time": time = L(t, ++i); break;
                case "hashfull": hashfull = I(t, ++i); break;
                case "wdl" when i + 3 < t.Length:
                    if (I(t, i + 1) is { } w && I(t, i + 2) is { } d && I(t, i + 3) is { } l) wdl = new EngineWdl(w, d, l);
                    i += 3; break;
                case "score" when i + 2 < t.Length:
                    string kind = t[++i];
                    int? value = I(t, ++i);
                    var bound = ScoreBound.Exact;
                    if (i + 1 < t.Length && t[i + 1] is "lowerbound" or "upperbound")
                        bound = t[++i] == "lowerbound" ? ScoreBound.Lower : ScoreBound.Upper;
                    if (value is { } v)
                        score = kind == "mate" ? new EngineScore(null, v, whiteToMove, bound)
                            : kind == "cp" ? new EngineScore(v, null, whiteToMove, bound) : null;
                    break;
                case "pv":
                    pv = [];
                    for (i++; i < t.Length && UciMove.TryParse(t[i], out var m); i++) pv.Add(m);
                    i--; break;
                case "string":
                    text = string.Join(' ', t.Skip(i + 1));
                    i = t.Length; break;
            }
        }
        return new EngineInfo(depth, sel, mpv, score, wdl, nodes, nps, tb, time, hashfull, pv, text);
    }

    /// <summary>Encode as a UCI <c>info</c> line from the side to move, the inverse of <see cref="Decode"/>.</summary>
    public string Encode()
    {
        var p = new List<string> { "info" };
        void Add(string k, long? v) { if (v is { } x) p.Add(k + " " + x.ToString(CultureInfo.InvariantCulture)); }
        Add("depth", Depth); Add("seldepth", SelDepth); Add("multipv", MultiPv);
        if (Score is { } s)
        {
            p.Add(s.Mate is { } m ? $"score mate {m}" : $"score cp {s.Cp}");
            if (s.Bound != ScoreBound.Exact) p.Add(s.Bound == ScoreBound.Lower ? "lowerbound" : "upperbound");
        }
        if (Wdl is { } w) p.Add($"wdl {w.Win} {w.Draw} {w.Loss}");
        Add("nodes", Nodes); Add("nps", Nps); Add("tbhits", TbHits); Add("time", TimeMs); Add("hashfull", HashFull);
        if (Pv is { Count: > 0 } pv) p.Add("pv " + string.Join(' ', pv.Select(static m => m.Text)));
        if (Text is not null) p.Add("string " + Text);
        return string.Join(' ', p);
    }
}

/// <summary><c>bestmove</c>: Move is null when the engine had none (<c>(none)</c> or <c>0000</c>).</summary>
public sealed record EngineBestMove(UciMove? Move, UciMove? Ponder)
{
    public static EngineBestMove? Decode(string line)
    {
        if (!line.StartsWith("bestmove", StringComparison.Ordinal)) return null;
        var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        UciMove? move = t.Length > 1 && UciMove.TryParse(t[1], out var m) ? m : null;
        int pi = Array.IndexOf(t, "ponder");
        UciMove? ponder = pi > 0 && pi + 1 < t.Length && UciMove.TryParse(t[pi + 1], out var pm) ? pm : null;
        return new EngineBestMove(move, ponder);
    }

    public string Encode() => "bestmove " + (Move?.Text ?? "0000") + (Ponder is { } p ? " ponder " + p.Text : "");
}

/// <summary>An advertised UCI option.</summary>
public sealed record UciOptionInfo(string Name, string Type, string Default, long? Min, long? Max, IReadOnlyList<string> Vars)
{
    public static UciOptionInfo? Decode(string line)
    {
        const string prefix = "option name ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        int typeAt = line.IndexOf(" type ", prefix.Length, StringComparison.Ordinal);
        if (typeAt < 0) return null;
        string rest = line[(typeAt + 6)..];
        string type = rest.Split(' ', 2)[0];
        string Field(string name)
        {
            int start = rest.IndexOf(" " + name + " ", StringComparison.Ordinal);
            if (start < 0) return "";
            start += name.Length + 2;
            int end = rest.Length;
            foreach (var marker in new[] { " min ", " max ", " var " })
            {
                int next = rest.IndexOf(marker, start, StringComparison.Ordinal);
                if (next >= 0) end = Math.Min(end, next);
            }
            return rest[start..end];
        }
        var vars = new List<string>();
        for (int at = rest.IndexOf(" var ", StringComparison.Ordinal); at >= 0; at = rest.IndexOf(" var ", at + 5, StringComparison.Ordinal))
        {
            int end = rest.IndexOf(" var ", at + 5, StringComparison.Ordinal);
            vars.Add(rest[(at + 5)..(end < 0 ? rest.Length : end)]);
        }
        return new UciOptionInfo(line[prefix.Length..typeAt], type, Field("default"),
            long.TryParse(Field("min"), out var min) ? min : null,
            long.TryParse(Field("max"), out var max) ? max : null, vars);
    }

    public void Validate(string value)
    {
        if (value.Contains('\n') || value.Contains('\r'))
            throw new ArgumentException($"Option {Name} must be a single UCI value.");
        if (Type == "spin" && (!long.TryParse(value, out var number)
            || Min.HasValue && number < Min || Max.HasValue && number > Max))
            throw new ArgumentOutOfRangeException(Name, $"The engine advertises {Name} range {Min}..{Max}.");
        if (Type == "check" && value is not ("true" or "false"))
            throw new ArgumentException($"Option {Name} is a check: true or false.");
        if (Type == "combo" && Vars.Count > 0 && !Vars.Contains(value, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Option {Name} accepts {string.Join('|', Vars)}.");
    }
}
