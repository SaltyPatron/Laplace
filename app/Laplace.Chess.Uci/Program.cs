using Laplace.Chess.Uci;

// The UCI read loop: one command per line on stdin, responses on stdout. Kept dead-simple so cutechess-cli
// and lichess-bot can drive the engine; all logic lives in the testable UciEngine.
var engine = new UciEngine();
string? line;
while ((line = Console.ReadLine()) is not null)
{
    if (!engine.Handle(line, Console.Out)) break;
    Console.Out.Flush();
}
