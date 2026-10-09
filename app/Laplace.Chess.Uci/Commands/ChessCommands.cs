using System.Globalization;
using Laplace.Chess.Service.Uci;
using Laplace.Chess.Uci.Engines;

namespace Laplace.Chess.Uci.Commands;

/// <summary>The commands' results, as data. <see cref="ChessCommands"/> produces them; a writer renders them.</summary>
public sealed record EngineListing(string Name, bool Available, string Profile, int MaxProcesses,
    IReadOnlyList<KeyValuePair<string, string>> ProfileOptions, EngineIdentity? Identity = null, string? Error = null);

public sealed record BestMoveResult(string Engine, string? BestMove, string? Ponder, EngineScore? Score, EngineReceipt Receipt);

public sealed record CompareEntry(string Engine, EngineAnalysis? Analysis, CommandError? Error);

public sealed record CommandError(string Kind, string Message)
{
    public static CommandError From(Exception ex) => ex switch
    {
        UciEngineException u => new(u.Failure.ToString().ToLowerInvariant(), u.Message),
        ArgumentException or FormatException => new("rejected", ex.Message),
        _ => new("failed", ex.Message),
    };
}

/// <summary>The non-UCI commands of laplace-uci: engines, analyse, bestmove, compare. Each returns data.</summary>
public static class ChessCommands
{
    public static IReadOnlyList<EngineListing> Engines(string? profile = null)
        => ChessEngineCatalog.Names.Select(name =>
        {
            ChessEngineSpec spec;
            try { spec = ChessEngineCatalog.Resolve(name, profile); }
            catch (UciEngineException ex) { return new EngineListing(name, false, profile ?? "play", 0, [], Error: ex.Message); }
            if (spec.Start is null) return new EngineListing(name, false, spec.Profile, spec.MaxProcesses, spec.ProfileOptions, Error: spec.Missing);
            try
            {
                // The handshake names the engine; nothing is loaded (Lc0 reads its network on isready, not uci).
                using var process = UciProcess.Start(spec.Start);
                return new EngineListing(name, true, spec.Profile, spec.MaxProcesses, spec.ProfileOptions, process.Identity);
            }
            catch (UciEngineException ex)
            {
                return new EngineListing(name, false, spec.Profile, spec.MaxProcesses, spec.ProfileOptions, Error: ex.Message);
            }
        }).ToList();

    public static EngineAnalysis Analyse(ChessCommandLine cmd, string engine, string defaultProfile = "analysis")
    {
        var spec = ChessEngineCatalog.Resolve(engine, cmd.Value("profile") ?? defaultProfile);
        var position = cmd.Position;
        var limits = cmd.Limits;
        using var session = EngineSession.Open(spec, cmd.Options);
        return session.Analyse(position, limits, cmd.MultiPv, cmd.Flags.Contains("warm") ? "warm" : "fresh");
    }

    public static BestMoveResult BestMove(ChessCommandLine cmd)
    {
        var analysis = Analyse(cmd, cmd.Engine, "play");
        return new BestMoveResult(cmd.Engine, analysis.BestMove.Move?.Text, analysis.BestMove.Ponder?.Text,
            analysis.Final?.Score, analysis.Receipt);
    }

    public static IReadOnlyList<CompareEntry> Compare(ChessCommandLine cmd)
    {
        var engines = cmd.Value("engines")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? ChessEngineCatalog.Names;
        _ = cmd.Position; _ = cmd.Limits; // reject a bad request before starting any engine
        return engines.Select(engine =>
        {
            try { return new CompareEntry(engine, Analyse(cmd, engine), null); }
            catch (Exception ex) when (ex is UciEngineException or ArgumentException or FormatException)
            {
                return new CompareEntry(engine, null, CommandError.From(ex));
            }
        }).ToList();
    }

    public const string Usage = """
        laplace-uci: the chess entry point.
          laplace-uci [uci] [--engine laplace|stockfish|lc0] [--profile P] [--option Name=Value]...
              a UCI engine on stdin/stdout (default: Laplace's own engine; stockfish/lc0 through their pinned profiles)
          laplace-uci engines [--profile P]                    the configured engines and their identities (JSON)
          laplace-uci analyse --engine E [--fen F] [--moves "e2e4 e7e5"] (--nodes N | --depth D | --movetime MS)
                              [--multipv K] [--profile P] [--option Name=Value]... [--warm]
          laplace-uci bestmove (same arguments; profile play)
          laplace-uci compare [--engines laplace,stockfish,lc0] (same arguments): the same limits on each engine
          Option names may be the engine's own or canonical: threads hash multipv wdl syzygy network backend overhead ponder substrate.

          laplace-uci match --engine1 E[:k=v...] --engine2 E[:k=v...] [--games 2] [--tc 10+0.1 | --nodes N] [--book 8moves_v3.pgn|PATH|none]
                            [--plies 16] [--seed N] [--concurrency 1] [--affinity CPUS] [--no-tb] [--maxmoves 200] [--out DIR] [--pgn FILE]
              a fastchess match; E is laplace, stockfish, lc0 or a UCI executable; keys nodes, profile, name, or a UCI option.
              Writes the games and receipt.json (conductor and engines by SHA-256, argv, book, seed) and prints the receipt.
          laplace-uci ladder calibrate --rungs sf:nodes=256,sf:nodes=512 [--games 40] [--concurrency 4] [--affinity CPUS] [--tc TC]
                                       [--book 8moves_v3.pgn] [--plies 16] [--srand 20261006] [--anchor NAME] [--anchor-elo 0] [--out DIR]
          laplace-uci ladder gauntlet --seed laplace:nodes=2000 --rungs sf:nodes=16,sf:nodes=32 [--ladder CALIBRATION_RUN] (same options)
          laplace-uci ladder rate RUN_DIR --anchor NAME [--anchor-elo 0] [--ladder CALIBRATION_RUN]
          laplace-uci ladder stockfish-receipt [--out FILE]
              rungs: sf:nodes=N[:syzygy=off]  lc0:nodes=N[:net=FILE][:backend=B]  laplace[:nodes=N][:substrate=off]
          laplace-uci check [--prefix DIR] [--uci EXE] [--check-latest] [--cutechess-gui] [--require-data] [--require-ladder]
              the installed chess tools and data, exercising the configured binaries (JSON; exit 1 when a required check fails)
          laplace-uci review PGN|DIR [--depth 4] [--max-games 20] [--json]
          laplace-uci lichess [--token T] [--engine laplace|stockfish|lc0|EXE] [--max-concurrent 4] [--speed S]... [--rated]
              the Lichess connector (one event stream per account: never beside the LaplaceLichess service)
          laplace-uci inspect PGN [--game N | --where Tag=Value] [--max-games N] [--all-positions] [--json]
          laplace-uci inspect "FEN" [--moves "e4 e5"]  |  inspect "e4 e5 Nf3" [--fen F]
              the tree the monorepo would store for the input, read-only, without a database
        """;
}
