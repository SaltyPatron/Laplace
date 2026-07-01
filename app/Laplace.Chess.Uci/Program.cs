using Laplace.Chess.Uci;



var engine = new UciEngine();
string? line;
while ((line = Console.ReadLine()) is not null)
{
    if (!engine.Handle(line, Console.Out)) break;
    Console.Out.Flush();
}
