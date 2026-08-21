using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>One ply of a replayed game: what was played, the board after it, and where that board lives.</summary>
public sealed record ChessPly(
    int Ply,
    string San,
    string Uci,
    string Fen,
    bool WhiteMoved,
    double? ClockSeconds,
    string PositionId);

/// <summary>A recorded game, replayed: the ply sequence plus whether clocks came with it.</summary>
public sealed record ChessReplayResult(
    string StartFen,
    IReadOnlyList<ChessPly> Plies,
    bool HasClocks,
    string? Truncated);

/// <summary>
/// Replays typed move trajectories or parses PGN supplied at an interchange boundary.
/// The stored record is the former: reusable move objects ordered by the reusable line's
/// physicality, with sparse playing-specific source annotations in parallel trajectories.
///
/// The positions are NOT reconstructed-and-thrown-away, though. Every board here is
/// hashed through ChessCompose to the same perfcache address used by the analyzer. A line's
/// evictable position projection carries those points without depositing a SQL entity tree for
/// every board or duplicating each adjacent pair as a MOVE consensus row.
///
/// SAN never gets parsed twice. Resolution goes through San.Resolve against the engine's
/// own legal-move list, the same call the analyzer and the book decomposer make.
/// </summary>
public static partial class ChessReplay
{
    // Movetext dialect: braced comments ({[%clk ...]}, annotations), NAGs ($1), move
    // numbers ("12." / "12..."), RAV variations, and the trailing result token. The book
    // decomposer's tokenizer handles the PROSE dialect (bare algebraic in running text)
    // and is deliberately not reused here — different input, different noise.
    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"\$\d+")]
    private static partial Regex NagRegex();

    [GeneratedRegex(@"\d+\.(\.\.)?")]
    private static partial Regex MoveNumberRegex();

    private static readonly string[] ResultTokens = ["1-0", "0-1", "1/2-1/2", "*"];

    /// <summary>
    /// Replay a movetext. Returns every ply it could legally play; if a token fails to
    /// resolve, the walk STOPS there and says so in <c>Truncated</c> rather than skipping
    /// it — a move that will not play means the boards after it are fiction, and serving
    /// fiction next to witnessed record is the one thing this must not do.
    /// </summary>
    public static ChessReplayResult Replay(string? movetext, int maxPlies = 1024)
    {
        var modality = new ChessModality();
        var state = modality.Initial();
        var startFen = state.Board.ToFen();
        if (string.IsNullOrWhiteSpace(movetext))
            return new ChessReplayResult(startFen, [], false, null);

        var tokens = Tokenize(movetext);
        var plies = new List<ChessPly>(Math.Min(tokens.Count, maxPlies));
        string? truncated = null;

        // ChessCompose.Gate guards process-global native compose state; take it once for
        // the whole walk rather than per ply.
        lock (ChessCompose.Gate)
        {
            foreach (var token in tokens)
            {
                if (plies.Count >= maxPlies)
                {
                    truncated = $"stopped at {maxPlies} plies";
                    break;
                }

                var legal = modality.LegalActions(state);
                var move = San.Resolve(state.Board, legal, token);
                if (move is null)
                {
                    truncated = $"could not resolve “{token}” at ply {plies.Count + 1}";
                    break;
                }

                bool whiteMoved = state.Board.WhiteToMove;
                string san = San.ToSan(state.Board, move.Value);
                state = modality.Apply(state, move.Value);
                var positionId = ChessCompose.PositionId(state.Board);

                plies.Add(new ChessPly(
                    plies.Count + 1, san, move.Value.ToUci(), state.Board.ToFen(),
                    whiteMoved, null, Convert.ToHexString(positionId.ToBytes()).ToLowerInvariant()));
            }
        }

        // Clocks are read from the ORIGINAL movetext, not reconstructed: PgnClocks only
        // returns a series when every ply carries a reading, so a partially-clocked game
        // reports no clocks rather than a series with invented gaps.
        var clocks = PgnClocks.SecondsRemaining(movetext, plies.Count);
        bool hasClocks = clocks.Length == plies.Count && plies.Count > 0;
        if (hasClocks)
            for (int i = 0; i < plies.Count; i++)
                plies[i] = plies[i] with { ClockSeconds = clocks[i] };

        return new ChessReplayResult(startFen, plies, hasClocks, truncated);
    }

    /// <summary>
    /// Replay a typed move trajectory and hand each board to <paramref name="visit"/>,
    /// starting with the initial position. Same matching law as <see cref="Replay"/> --
    /// a move id is resolved only against the legal actions of the current board -- but
    /// no SAN, no FEN and no position id are produced.
    ///
    /// This exists because folds over the corpus do not want strings. LearnedPst read
    /// 2,000 games in 33.6s through Replay(), almost all of it San.ToSan + Board.ToFen +
    /// PositionId per ply and then Board.FromFen to undo the FEN it had just built.
    /// Returns false if the line did not resolve, so a caller can drop a partial game
    /// rather than fold half of it.
    ///
    /// The board handed to the visitor is REUSED between plies. Callers accumulate from
    /// it; they must not retain it.
    /// </summary>
    public static bool ForEachBoard(
        IReadOnlyList<Hash128> moveIds, Action<Board> visit,
        string? startFen = null, int maxPlies = 1024)
    {
        ArgumentNullException.ThrowIfNull(visit);
        var board = string.IsNullOrWhiteSpace(startFen)
            ? Board.FromFen(ChessModality.StartFen)
            : Board.FromFen(startFen);
        lock (ChessCompose.Gate)
        {
            visit(board);
            int limit = Math.Min(moveIds.Count, maxPlies);
            for (int i = 0; i < limit; i++)
            {
                var legal = MoveGen.Legal(board);
                ChessMove? matched = null;
                foreach (var move in legal)
                {
                    Piece moving = board.Squares[move.From];
                    if (ChessCompose.MoveId(moving, move) != moveIds[i]) continue;
                    if (matched is not null) return false;   // ambiguous typed move
                    matched = move;
                }
                if (matched is null) return false;           // does not resolve
                MoveApply.Make(board, matched.Value);
                visit(board);
            }
        }
        return true;
    }

    /// <summary>
    /// Replay a line's typed move trajectory. Each move id is matched only against the
    /// legal actions from the current board; PGN/SAN is generated output, never stored identity.
    /// </summary>
    public static ChessReplayResult Replay(
        IReadOnlyList<Hash128> moveIds, string? startFen = null, int maxPlies = 1024)
    {
        var board = string.IsNullOrWhiteSpace(startFen)
            ? Board.FromFen(ChessModality.StartFen)
            : Board.FromFen(startFen);
        string canonicalStart = board.ToFen();
        var plies = new List<ChessPly>(Math.Min(moveIds.Count, maxPlies));
        string? truncated = null;
        lock (ChessCompose.Gate)
        {
            for (int i = 0; i < moveIds.Count; i++)
            {
                if (plies.Count >= maxPlies)
                {
                    truncated = $"stopped at {maxPlies} plies";
                    break;
                }
                var legal = MoveGen.Legal(board);
                ChessMove? matched = null;
                foreach (var move in legal)
                {
                    Piece moving = board.Squares[move.From];
                    if (ChessCompose.MoveId(moving, move) != moveIds[i]) continue;
                    if (matched is not null)
                    {
                        truncated = $"ambiguous typed move at ply {i + 1}";
                        break;
                    }
                    matched = move;
                }
                if (truncated is not null) break;
                if (matched is null)
                {
                    truncated = $"typed move does not resolve at ply {i + 1}";
                    break;
                }

                var mv = matched.Value;
                bool whiteMoved = board.WhiteToMove;
                string san = San.ToSan(board, mv);
                MoveApply.Make(board, mv);
                var positionId = ChessCompose.PositionId(board);
                plies.Add(new ChessPly(
                    i + 1, san, mv.ToUci(), board.ToFen(), whiteMoved, null,
                    Convert.ToHexString(positionId.ToBytes()).ToLowerInvariant()));
            }
        }
        return new ChessReplayResult(canonicalStart, plies, false, truncated);
    }

    /// <summary>Apply aligned source comment annotations to generated replay output.</summary>
    public static ChessReplayResult ApplyClockComments(
        ChessReplayResult replay, IReadOnlyList<string?> comments)
    {
        if (replay.Plies.Count == 0 || comments.Count != replay.Plies.Count) return replay;
        var text = new System.Text.StringBuilder(replay.Plies.Count * 16);
        for (int i = 0; i < replay.Plies.Count; i++)
        {
            if (text.Length > 0) text.Append(' ');
            text.Append(replay.Plies[i].San);
            if (!string.IsNullOrWhiteSpace(comments[i]))
                text.Append(" { ").Append(comments[i]).Append(" }");
        }
        var clocks = PgnClocks.SecondsRemaining(text.ToString(), replay.Plies.Count);
        if (clocks.Length != replay.Plies.Count) return replay;
        var plies = replay.Plies.ToArray();
        for (int i = 0; i < plies.Length; i++)
            plies[i] = plies[i] with { ClockSeconds = clocks[i] };
        return replay with { Plies = plies, HasClocks = true };
    }

    public static string ToMovetext(ChessReplayResult replay, string? result)
    {
        var sb = new System.Text.StringBuilder(replay.Plies.Count * 8 + 16);
        for (int i = 0; i < replay.Plies.Count; i++)
        {
            if ((i & 1) == 0)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append((i / 2) + 1).Append('.').Append(' ');
            }
            else sb.Append(' ');
            sb.Append(replay.Plies[i].San);
        }
        if (!string.IsNullOrWhiteSpace(result))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(result);
        }
        return sb.ToString();
    }

    /// <summary>Movetext to bare SAN tokens, in order.</summary>
    private static List<string> Tokenize(string movetext)
    {
        // Comments go first so a SAN-looking fragment inside one can never be played.
        string s = CommentRegex().Replace(movetext, " ");
        s = NagRegex().Replace(s, " ");
        s = StripVariations(s);
        s = MoveNumberRegex().Replace(s, " ");

        var outv = new List<string>();
        foreach (var raw in s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (t.Length == 0 || Array.IndexOf(ResultTokens, t) >= 0) continue;
            outv.Add(t);
        }
        return outv;
    }

    /// <summary>
    /// Drop parenthesised RAV variations, nesting included. A variation is a line that was
    /// NOT played; replaying it would produce boards this game never reached.
    /// </summary>
    private static string StripVariations(string s)
    {
        if (!s.Contains('(')) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        int depth = 0;
        foreach (char c in s)
        {
            if (c == '(') { depth++; continue; }
            if (c == ')') { if (depth > 0) depth--; continue; }
            if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }
}
