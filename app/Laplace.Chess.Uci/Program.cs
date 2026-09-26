using Laplace.Chess.Uci;
using Laplace.Ops;
using Microsoft.Extensions.Logging;

// stdout is the UCI wire protocol, so diagnostics go only to the ops file sink (read back
// through ops.app_log), never to stdout or stderr. Engine messages ride the protocol as
// `info string` lines.
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
