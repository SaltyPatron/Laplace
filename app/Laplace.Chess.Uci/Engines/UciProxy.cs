using System.Diagnostics;
using System.Text.Json;
using Laplace.Chess.Service.Uci;

namespace Laplace.Chess.Uci.Engines;

/// <summary>
/// <c>laplace-uci uci --engine stockfish|lc0</c>: a plain UCI engine to any GUI or conductor that is another engine
/// underneath. The engine is started and configured from the catalog (its pinned profile, network and backend); the
/// GUI's commands pass through; the engine's handshake is replayed for <c>uci</c>. Laplace's receipt rides the
/// protocol as <c>info string</c>: the engine's identity before the first readyok, and a job receipt before each
/// bestmove.
/// </summary>
public static class UciProxy
{
    public static int Run(ChessEngineSpec spec, IReadOnlyDictionary<string, string> overrides, TextReader input, TextWriter output)
    {
        var gate = new object();
        void Write(string line) { lock (gate) { output.WriteLine(line); output.Flush(); } }

        EngineSession session;
        try { session = EngineSession.Open(spec, overrides); }
        catch (UciEngineException ex)
        {
            // Still a UCI engine to the GUI: answer the handshake with the reason, never another engine.
            Write($"info string laplace-uci: {spec.Name} {ex.Failure.ToString().ToLowerInvariant()}: {ex.Message}");
            return 2;
        }

        using (session)
        {
            var engine = session.Process;
            var sent = engine.OptionsSent.ToList();
            ChessPosition? position = null;
            string? positionText = null;
            string go = "";
            long? lastNodes = null;
            var clock = new Stopwatch();
            bool announced = false;

            var relay = engine.RelayOutput(line =>
            {
                if (line == "readyok" && !announced)
                {
                    announced = true;
                    Write("info string " + EngineReceipt.IdentityPrefix + JsonSerializer.Serialize(engine.Identity, UciJson.Options));
                }
                if (line.StartsWith("bestmove", StringComparison.Ordinal))
                {
                    clock.Stop();
                    var key = position is not null ? NotationIdentityResolver.Instance.ResolvePosition(position)
                        : new ChessPositionKey(positionText ?? "", []);
                    List<KeyValuePair<string, string>> options;
                    lock (gate) options = sent.ToList();
                    Write(new EngineReceipt(Environment.MachineName, engine.Identity, options, key, go, "gui",
                        clock.ElapsedMilliseconds, lastNodes).EncodeInfoString());
                }
                else if (line.StartsWith("info ", StringComparison.Ordinal) && EngineInfo.Decode(line, true)?.Nodes is { } n)
                    lastNodes = n;
                Write(line);
            });

            string? command;
            while ((command = input.ReadLine()) is not null)
            {
                string trimmed = command.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed == "uci")
                {
                    lock (gate)
                    {
                        foreach (var line in engine.HandshakeLines) output.WriteLine(line);
                        output.WriteLine("uciok");
                        output.Flush();
                    }
                    continue;
                }
                if (trimmed == "quit") break;
                if (trimmed.StartsWith("position ", StringComparison.Ordinal))
                {
                    positionText = trimmed;
                    position = TryPosition(trimmed);
                }
                else if (trimmed.StartsWith("go", StringComparison.Ordinal))
                {
                    go = trimmed;
                    lastNodes = null;
                    clock.Restart();
                }
                else if (trimmed.StartsWith("setoption name ", StringComparison.Ordinal))
                {
                    int v = trimmed.IndexOf(" value ", StringComparison.Ordinal);
                    string name = v < 0 ? trimmed[15..] : trimmed[15..v];
                    lock (gate)
                    {
                        sent.RemoveAll(o => o.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
                        sent.Add(new(name, v < 0 ? "" : trimmed[(v + 7)..]));
                    }
                }
                try { engine.SendRaw(trimmed); }
                catch (UciEngineException) { break; }
            }
            try { engine.SendRaw("quit"); } catch (UciEngineException) { }
            engine.WaitForExit(TimeSpan.FromSeconds(5));
            relay.Wait(TimeSpan.FromSeconds(2));
        }
        return 0;
    }

    private static ChessPosition? TryPosition(string command)
    {
        var t = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int fen = Array.IndexOf(t, "fen"), moves = Array.IndexOf(t, "moves");
        try
        {
            return ChessPosition.From(
                fen < 0 ? null : string.Join(' ', t.Skip(fen + 1).TakeWhile(static x => x != "moves")),
                moves < 0 ? null : t[(moves + 1)..]);
        }
        catch (FormatException) { return null; }
    }
}
