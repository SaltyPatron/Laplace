using System.Text.Json;
using Laplace.Chess.Service.Uci;
using Laplace.Chess.Uci;
using Laplace.Chess.Uci.Commands;
using Laplace.Chess.Uci.Engines;
using Laplace.Chess.Uci.Lab;
using Laplace.Ops;
using Microsoft.Extensions.Logging;

ILoggerFactory? loggerFactory = null;
int exitCode = 1;
try
{
    loggerFactory = LaplaceLogging.FileOnly("uci");
    Npgsql.NpgsqlLoggingConfiguration.InitializeLogging(loggerFactory, parameterLoggingEnabled: false);
    exitCode = await DispatchAsync(args, loggerFactory);
}
catch (Exception ex)
{
    LaplaceLogging.ReportProcessFailure("laplace-uci", ex,
        failure => loggerFactory?.CreateLogger("session").LogCritical(failure, "UCI process terminated unexpectedly."));
    exitCode = 1;
}
return LaplaceLogging.CompleteProcess(loggerFactory, "laplace-uci", exitCode);

static async Task<int> DispatchAsync(string[] args, ILoggerFactory loggerFactory)
{
    var log = loggerFactory.CreateLogger("session");
    ChessCommandLine cmd;
    try { cmd = ChessCommandLine.Parse(args); }
    catch (ArgumentException ex)
    {
        log.LogWarning(ex, "UCI command arguments are invalid.");
        Console.Error.WriteLine(ex.Message);
        Console.Error.WriteLine(ChessCommands.Usage);
        return 64;
    }

    if (cmd.Verb == "uci") return RunUciLoop(cmd, log);

    var json = new JsonSerializerOptions(UciJson.Options) { WriteIndented = true };
    bool textOutput = (cmd.Verb is "review" or "lichess" or "help"
        || (cmd.Verb == "ladder" && cmd.Positionals.FirstOrDefault() != "stockfish-receipt")) && !cmd.Flag("json");
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
                Console.Out.WriteLine(!textOutput ? ladder.Receipt?.ToJsonString(json) ?? ladder.Table : ladder.Table);
                if (textOutput && ladder.Receipt is not null && ladder.Run is not null) Console.Out.WriteLine($"run: {ladder.Run}");
                if (textOutput && ladder.Warning is not null) Console.Out.WriteLine(ladder.Warning);
                return ladder.Failed ? 1 : 0;
            }
            case "help":
                Console.Out.WriteLine(ChessCommands.Usage);
                return 0;
            default:
                object result = cmd.Verb switch
                {
                    "engines" => ChessCommands.Engines(cmd.Value("profile")),
                    "analyse" => ChessCommands.Analyse(cmd, cmd.Engine),
                    "bestmove" => ChessCommands.BestMove(cmd),
                    "compare" => ChessCommands.Compare(cmd),
                    _ => ChessCommands.Usage,
                };
                Console.Out.WriteLine(result is string text ? text : JsonSerializer.Serialize(result, json));
                return 0;
        }
    }
    catch (Exception ex) when (ex is UciEngineException or ArgumentException or FormatException or IOException or InvalidOperationException
                                   or KeyNotFoundException or TimeoutException or UnauthorizedAccessException)
    {
        loggerFactory.CreateLogger("command").LogError(ex, "UCI command {Command} failed.", cmd.Verb);
        if (textOutput) Console.Error.WriteLine($"laplace-uci {cmd.Verb}: {ex.Message}");
        else Console.Out.WriteLine(JsonSerializer.Serialize(new { error = CommandError.From(ex) }, json));
        return 2;
    }
}

static int RunUciLoop(ChessCommandLine cmd, ILogger log)
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
                log.LogError(ex, "UCI engine selection failed.");
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
