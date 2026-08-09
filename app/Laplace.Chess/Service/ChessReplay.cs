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
/// Turns a recorded movetext back into the sequence of boards it describes.
///
/// This is what a game "is" in the substrate, and it is worth being exact about. The
/// record layer stores the movetext VERBATIM as content — not a parse, not a blob: a
/// tier-4 entity whose constituent chain rebuilds the original PGN bytes from its id
/// alone. Per-ply record consensus.edges(HAS_PLY / HAS_SAN) were deliberately removed, because a
/// ply of one game can never corroborate a ply of another, so each was a permanently
/// single-witness consensus cell. The movetext plus replay reconstructs all of them,
/// which is exactly what this does.
///
/// The positions are NOT reconstructed-and-thrown-away, though. Every board here is
/// hashed through ChessCompose to the same content address the analyzer deposited, so a
/// replayed ply lands on a real Chess_Position entity — one of ~5.2M, each carrying S³
/// geometry and rated MOVE edges to its continuations. Replaying a game is therefore a
/// walk INTO the substrate, not a private computation beside it: at any ply the caller
/// can ask what the rest of the corpus played from that same board.
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
                var positionId = ChessCompose.PositionId(modality.StateKey(state));

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
