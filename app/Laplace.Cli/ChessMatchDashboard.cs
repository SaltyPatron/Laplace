using System.Collections.Concurrent;
using Laplace.Chess.Service;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Laplace.Cli;

/// <summary>
/// Terminal dashboard for engine-vs-engine matches (GH #604): drives the existing
/// ChessLabService cutechess job and renders its live ChessLabEvent stream — the SAME
/// stream the web lab UI consumes — as a board + progress + metrics + log tail via
/// AnsiConsole.Live. Games stream into the substrate through the job's own re-ingestion
/// (ChessLabRunners.RunCutechessAsync → ChessPgnIngestor); this surface only watches.
/// </summary>
internal static class ChessMatchDashboard
{
    public static async Task<int> RunAsync(IReadOnlyDictionary<string, string> config, CancellationToken ct)
    {
        if (!CutechessRunner.ProbeCatalog(out var cc, out var sf, out var qt))
        {
            AnsiConsole.MarkupLine(
                $"[red]chess lab binaries missing[/] — cutechess={cc} stockfish={sf} qt={qt}. "
                + "Build them with scripts/bootstrap-chess-lab.sh (paths in deploy/secrets/chess-lab.env).");
            return 2;
        }

        var lab = new ChessLabService();
        var jobId = lab.StartJob(ChessLabJobKind.Cutechess, config);
        if (jobId is null)
        {
            AnsiConsole.MarkupLine("[red]could not start match job[/] (concurrency cap reached).");
            return 1;
        }
        var reader = lab.EventReader(jobId);
        if (reader is null) return 1;

        // Ctrl+C cancels the job cleanly (kills cutechess, keeps the PGN artifact) rather than
        // orphaning the child process.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; lab.StopJob(jobId); linked.Cancel(); };
        Console.CancelKeyPress += onCancel;

        var state = new MatchState();
        int exit = 0;
        try
        {
            await AnsiConsole.Live(state.Render())
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async ctx =>
                {
                    ctx.Refresh();
                    try
                    {
                        await foreach (var evt in reader.ReadAllAsync(linked.Token))
                        {
                            state.Apply(evt);
                            ctx.UpdateTarget(state.Render());
                            ctx.Refresh();
                        }
                    }
                    catch (OperationCanceledException) { /* stop requested — show final frame */ }
                    ctx.UpdateTarget(state.Render());
                    ctx.Refresh();
                });
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        if (state.Final == ChessLabJobState.Failed) exit = 1;
        return exit;
    }

    // Accumulates the latest frame across the event stream. Single-consumer (the Live loop),
    // so no locking beyond the bounded log queue.
    private sealed class MatchState
    {
        private string _fen = Laplace.Modality.Chess.ChessModality.StartFen;
        private string? _white, _black;
        private int _game, _ply, _done, _total;
        private readonly Dictionary<string, double> _metrics = new();
        private readonly ConcurrentQueue<(string Level, string Msg)> _log = new();
        public ChessLabJobState? Final { get; private set; }
        private string? _finalMsg;

        public void Apply(ChessLabEvent evt)
        {
            switch (evt)
            {
                case ChessLabBoardEvent b:
                    _fen = b.Fen; _game = b.Game; _ply = b.Ply;
                    _white = b.White ?? _white; _black = b.Black ?? _black;
                    break;
                case ChessLabProgressEvent p:
                    _done = p.Done; _total = p.Total;
                    break;
                case ChessLabMetricEvent m:
                    _metrics[m.Name] = m.Value;
                    break;
                case ChessLabLogEvent l:
                    _log.Enqueue((l.Level, l.Message));
                    while (_log.Count > 10) _log.TryDequeue(out _);
                    break;
                case ChessLabDoneEvent d:
                    Final = d.FinalState; _finalMsg = d.Message;
                    break;
            }
        }

        public IRenderable Render()
        {
            var header = new Markup(
                $"[bold]Laplace[/] vs [bold]Stockfish[/]   "
                + (_total > 0 ? $"game [yellow]{_done}[/]/[yellow]{_total}[/]" : "starting…")
                + (_game > 0 ? $"   ply [grey]{_ply}[/]" : ""));

            var rows = new List<IRenderable> { header, Board(_fen) };

            var metricLine = string.Join("   ", _metrics.Select(kv =>
                $"[grey]{Markup.Escape(kv.Key)}[/] [aqua]{kv.Value:0.##}[/]"));
            if (metricLine.Length > 0) rows.Add(new Markup(metricLine));

            if (Final is { } f)
            {
                var color = f == ChessLabJobState.Completed ? "green" : f == ChessLabJobState.Failed ? "red" : "yellow";
                rows.Add(new Markup($"[{color}]{f}[/]{(_finalMsg is null ? "" : " — " + Markup.Escape(_finalMsg))}"));
            }

            var logText = string.Join("\n", _log.Select(e =>
                $"[{(e.Level == "error" ? "red" : "grey")}]{Markup.Escape(e.Msg)}[/]"));
            var logPanel = new Panel(new Markup(logText.Length == 0 ? "[grey]…[/]" : logText))
                .Header("log").Border(BoxBorder.Rounded).Expand();
            rows.Add(logPanel);

            return new Rows(rows);
        }

        // FEN piece-placement -> an 8x8 board. Filled glyphs for both sides, side by fg colour
        // (white = bright white, black = blue), squares alternating so the grid reads even when
        // a square is empty. Rank 8 at the top, file labels beneath.
        private static IRenderable Board(string fen)
        {
            string placement = fen.Split(' ')[0];
            var ranks = placement.Split('/');

            var grid = new Grid();
            grid.AddColumn(); // rank label
            for (int f = 0; f < 8; f++) grid.AddColumn();

            for (int r = 0; r < ranks.Length && r < 8; r++)
            {
                var cells = new List<IRenderable> { new Markup($"[grey]{8 - r}[/]") };
                int file = 0;
                foreach (char c in ranks[r])
                {
                    if (char.IsDigit(c))
                    {
                        for (int k = 0; k < c - '0' && file < 8; k++, file++)
                            cells.Add(Square(file, r, ' ', false));
                    }
                    else
                    {
                        bool white = char.IsUpper(c);
                        cells.Add(Square(file, r, Glyph(char.ToLower(c)), white));
                        file++;
                    }
                }
                while (file < 8) { cells.Add(Square(file, r, ' ', false)); file++; }
                grid.AddRow(cells.ToArray());
            }

            var files = new List<IRenderable> { new Markup(" ") };
            foreach (char f in "abcdefgh") files.Add(new Markup($"[grey] {f} [/]"));
            grid.AddRow(files.ToArray());
            return grid;
        }

        private static IRenderable Square(int file, int rank, char glyph, bool white)
        {
            bool light = (file + rank) % 2 == 0;
            string bg = light ? "grey42" : "grey19";
            string fg = white ? "white" : "blue";
            return new Markup($"[{fg} on {bg}] {glyph} [/]");
        }

        private static char Glyph(char lower) => lower switch
        {
            'k' => '♚', 'q' => '♛', 'r' => '♜', 'b' => '♝', 'n' => '♞', 'p' => '♟', _ => '?',
        };
    }
}
