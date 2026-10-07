using System.Text.Json;
using Laplace.Chess.Service.Uci;
using Laplace.Chess.Uci;
using Laplace.Chess.Uci.Commands;
using Laplace.Chess.Uci.Engines;
using Laplace.Ops;
using Microsoft.Extensions.Logging;

// laplace-uci: the chess entry point. With no command (or `uci`) it is a UCI engine on stdin/stdout, so a GUI or a
// conductor starts it with no arguments; the other commands print JSON. stdout is the wire protocol in UCI mode, so
// diagnostics go only to the ops file sink (read back through ops.app_log), and engine messages ride the protocol as
// `info string` lines.
using var loggerFactory = LaplaceLogging.FileOnly("uci");
var log = loggerFactory.CreateLogger("session");

ChessCommandLine cmd;
try { cmd = ChessCommandLine.Parse(args); }
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(ChessCommands.Usage);
    return 64;
}

if (cmd.Verb == "uci")
{
    log.LogInformation("uci session started ({Engine})", cmd.Engine);
    try
    {
        if (!cmd.Engine.Equals("laplace", StringComparison.OrdinalIgnoreCase))
        {
            ChessEngineSpec spec;
            try { spec = ChessEngineCatalog.Resolve(cmd.Engine, cmd.Value("profile")); }
            catch (UciEngineException ex)
            {
                Console.Out.WriteLine($"info string laplace-uci: {ex.Message}");
                return 2;
            }
            return UciProxy.Run(spec, cmd.Options, Console.In, Console.Out);
        }

        var engine = new UciEngine();
        foreach (var (name, value) in cmd.Options) engine.Handle($"setoption name {name} value {value}", TextWriter.Null);
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (!engine.Handle(line, Console.Out)) break;
            Console.Out.Flush();
        }
        return 0;
    }
    catch (Exception ex)
    {
        log.LogError(ex, "uci loop terminated abnormally");
        throw;
    }
    finally
    {
        log.LogInformation("uci session ended");
    }
}

object result;
int exit = 0;
try
{
    result = cmd.Verb switch
    {
        "engines" => ChessCommands.Engines(cmd.Value("profile")),
        "analyse" => ChessCommands.Analyse(cmd, cmd.Engine),
        "bestmove" => ChessCommands.BestMove(cmd),
        "compare" => ChessCommands.Compare(cmd),
        _ => ChessCommands.Usage,
    };
}
catch (Exception ex) when (ex is UciEngineException or ArgumentException or FormatException)
{
    result = new { error = CommandError.From(ex) };
    exit = 2;
}
if (result is string text) Console.Out.WriteLine(text);
else Console.Out.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(UciJson.Options) { WriteIndented = true }));
return exit;
