using System.Text.Json;
using Laplace.Chess.Service.Uci;
using Laplace.Chess.Uci;
using Laplace.Chess.Uci.Commands;
using Laplace.Chess.Uci.Engines;
using Laplace.Chess.Uci.Lab;
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


// The other commands return data; this writes it: JSON for the engine and lab commands, readable text for inspect and
// review unless --json, the ladder's own progress and table.
var json = new JsonSerializerOptions(UciJson.Options) { WriteIndented = true };
bool textOutput = cmd.Verb is "inspect" or "review" or "lichess" or "help" || (cmd.Verb == "ladder" && cmd.Positionals.FirstOrDefault() != "stockfish-receipt");
textOutput &= !cmd.Flag("json");
try
{
    switch (cmd.Verb)
    {
        case "lichess":
        {
            var settings = LichessCommand.Parse(cmd);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Console.Out.WriteLine("\n[lichess] stopping after in-flight games finish...");
                cts.Cancel();
            };
            return await LichessCommand.RunAsync(settings, Console.Out, cts.Token);
        }
        case "inspect":
        {
            var report = InspectCommand.Run(cmd);
            Console.Out.WriteLine(textOutput ? InspectCommand.Render(report) : JsonSerializer.Serialize(report, json));
            return 0;
        }
        case "review":
        {
            var review = ReviewCommand.Run(cmd);
            Console.Out.WriteLine(textOutput ? ReviewCommand.Render(review) : JsonSerializer.Serialize(review, json));
            return 0;
        }
        case "check":
        {
            var (report, code) = CheckCommand.Run(cmd);
            Console.Out.WriteLine(report.ToJsonString(json));
            return code;
        }
        case "match":
        {
            var receipt = MatchCommand.Run(cmd);
            Console.Out.WriteLine(receipt.ToJsonString(json));
            return receipt["match"]?["exit"]?.GetValue<int>() is 0 ? 0 : 1;
        }
        case "ladder":
        {
            var ladder = LadderCommand.Run(cmd, progress: line => { Console.Out.WriteLine(line); Console.Out.Flush(); });
            if (!textOutput) Console.Out.WriteLine(ladder.Receipt?.ToJsonString(json) ?? ladder.Table);
            else
            {
                Console.Out.WriteLine(ladder.Table);
                if (ladder.Receipt is not null && ladder.Run is not null) Console.Out.WriteLine($"run: {ladder.Run}");
                if (ladder.Warning is not null) Console.Out.WriteLine(ladder.Warning);
            }
            return ladder.Failed ? 1 : 0;
        }
        case "help":
            Console.Out.WriteLine(ChessCommands.Usage);
            return 0;
    }

    object result = cmd.Verb switch
    {
        "engines" => ChessCommands.Engines(cmd.Value("profile")),
        "analyse" => ChessCommands.Analyse(cmd, cmd.Engine),
        "bestmove" => ChessCommands.BestMove(cmd),
        "compare" => ChessCommands.Compare(cmd),
        _ => ChessCommands.Usage,
    };
    if (result is string text) Console.Out.WriteLine(text);
    else Console.Out.WriteLine(JsonSerializer.Serialize(result, json));
    return 0;
}
catch (Exception ex) when (ex is UciEngineException or ArgumentException or FormatException or IOException or InvalidOperationException
                               or KeyNotFoundException or TimeoutException or UnauthorizedAccessException)
{
    if (textOutput) Console.Error.WriteLine($"laplace-uci {cmd.Verb}: {ex.Message}");
    else Console.Out.WriteLine(JsonSerializer.Serialize(new { error = CommandError.From(ex) }, json));
    return 2;
}
