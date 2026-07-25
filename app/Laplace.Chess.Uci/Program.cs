using Laplace.Chess.Uci;
using Laplace.Engine.Core.Ops;
using Microsoft.Extensions.Logging;

// stdout is the UCI wire protocol — diagnostics go to the CSV ops sink ONLY (FileOnly),
// never to stdout or stderr, so cutechess sees nothing but UCI. Read back via ops.app_log
// (GH #602). Engine-level messages still ride the protocol as `info string` lines.
using var loggerFactory = LaplaceLogging.FileOnly("uci");
var log = loggerFactory.CreateLogger("session");
log.LogInformation("uci session started");

var engine = new UciEngine();
try
{
    string? line;
    while ((line = Console.ReadLine()) is not null)
    {
        if (!engine.Handle(line, Console.Out)) break;
        Console.Out.Flush();
    }
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
