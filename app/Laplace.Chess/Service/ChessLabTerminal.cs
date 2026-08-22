using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Laplace.Chess.Service;

/// <summary>
/// Well-known terminal channel names — the <c>stream</c> field on the wire.
/// Free strings rather than an enum for the same reason <see cref="ChessLabLogEvent"/>
/// takes a string level: this crosses a JSON boundary where the viewer filters on it.
/// </summary>
public static class ChessLabStream
{
    /// <summary>The exact argv the job launched, rendered by the viewer as a shell prompt line.</summary>
    public const string Command = "command";

    /// <summary>Child-process stdout that is not engine traffic.</summary>
    public const string Stdout = "stdout";

    /// <summary>Child-process stderr.</summary>
    public const string Stderr = "stderr";

    /// <summary>UCI traffic between the harness and one engine (Engine and Direction are set).</summary>
    public const string Uci = "uci";

    /// <summary>The lab runner's own annotations — exit codes, ingest, cancellation.</summary>
    public const string Runner = "runner";
}

/// <summary>Direction of a <see cref="ChessLabStream.Uci"/> line, from the harness's point of view.</summary>
public static class ChessLabDirection
{
    /// <summary>Harness to engine (cutechess prints these with a leading <c>&gt;</c>).</summary>
    public const string Send = "send";

    /// <summary>Engine to harness (leading <c>&lt;</c>).</summary>
    public const string Recv = "recv";
}

/// <summary>One line of raw process I/O from a lab job.</summary>
/// <param name="Seq">
/// Monotonic per-job line number. Gaps are meaningful: they are exactly the lines a
/// viewer missed, either to ring eviction before it connected or to backpressure while
/// it was connected, so the viewer can render the elision instead of silently lying.
/// </param>
public sealed record ChessLabTerminalLine(
    long Seq,
    DateTimeOffset At,
    string Stream,
    string Text,
    string? Engine = null,
    string? Direction = null);

/// <summary>
/// The raw transcript of a lab job: a bounded scrollback ring plus live fan-out.
///
/// The structured event channel (<see cref="ChessLabService.EventReader"/>) cannot carry
/// this traffic. It is a single consumed channel — a second viewer steals frames from the
/// first, a viewer that connects late sees nothing that already happened, and a burst of
/// UCI chatter evicts the progress and result events that share it. A transcript needs the
/// opposite properties, so it gets its own structure: every subscriber sees the same lines,
/// a late subscriber gets the scrollback first, and a stalled subscriber degrades to a
/// visible gap rather than stalling the match.
/// </summary>
public sealed class ChessLabTerminal
{
    /// <summary>Per-subscriber buffer. Overflow drops oldest — see the Seq contract.</summary>
    private const int SubscriberBuffer = 2048;

    private readonly object _gate = new();
    private readonly ChessLabTerminalLine[] _ring;
    private readonly List<Channel<ChessLabTerminalLine>> _subscribers = [];
    private int _start;
    private int _count;
    private long _nextSeq;
    private bool _completed;

    public ChessLabTerminal(int capacity = 4000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _ring = new ChessLabTerminalLine[capacity];
    }

    /// <summary>Scrollback depth. Older lines are evicted, never renumbered.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Lines ever appended, whether or not they are still retained.</summary>
    public long Total { get { lock (_gate) return _nextSeq; } }

    /// <summary>Lines still in the ring.</summary>
    public int Retained { get { lock (_gate) return _count; } }

    public bool IsCompleted { get { lock (_gate) return _completed; } }

    public ChessLabTerminalLine Append(string stream, string text, string? engine = null, string? direction = null)
    {
        ChessLabTerminalLine line;
        Channel<ChessLabTerminalLine>[] subscribers;
        lock (_gate)
        {
            line = new ChessLabTerminalLine(_nextSeq++, DateTimeOffset.UtcNow, stream, text, engine, direction);
            int slot = (_start + _count) % _ring.Length;
            if (_count == _ring.Length) _start = (_start + 1) % _ring.Length;
            else _count++;
            _ring[slot] = line;
            subscribers = [.. _subscribers];
        }

        // Outside the lock, and never blocking: a browser that stopped reading must not be
        // able to stall the engines. DropOldest turns that into a Seq gap the viewer draws.
        foreach (var sub in subscribers) sub.Writer.TryWrite(line);
        return line;
    }

    /// <summary>
    /// One line as plain text — the shape both the on-disk transcript and the download use,
    /// so a saved run and a streamed one read identically.
    /// </summary>
    public static string Format(ChessLabTerminalLine line)
    {
        string tag = line.Engine is { Length: > 0 } engine
            ? $"[{line.Stream}/{engine}{(line.Direction == ChessLabDirection.Send ? " >" : " <")}]"
            : $"[{line.Stream}]";
        return $"{line.At.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)} {tag} {line.Text}";
    }

    /// <summary>Retained lines with <c>Seq &gt; afterSeq</c>, oldest first.</summary>
    public IReadOnlyList<ChessLabTerminalLine> Snapshot(long afterSeq = -1)
    {
        lock (_gate) return SnapshotLocked(afterSeq);
    }

    /// <summary>
    /// Scrollback then live, as one sequence. Resumable: pass the last Seq the caller
    /// already rendered and it picks up from there, replaying whatever the ring still holds.
    /// </summary>
    public async IAsyncEnumerable<ChessLabTerminalLine> ReadAsync(
        long afterSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        Channel<ChessLabTerminalLine> sub;
        List<ChessLabTerminalLine> backlog;
        lock (_gate)
        {
            // Snapshot and subscribe under one lock so a line appended between the two
            // is delivered by the channel rather than lost between them. Anything the
            // channel then re-delivers from the backlog window is filtered by Seq below.
            backlog = SnapshotLocked(afterSeq);
            sub = Channel.CreateBounded<ChessLabTerminalLine>(new BoundedChannelOptions(SubscriberBuffer)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
            if (_completed) sub.Writer.TryComplete();
            else _subscribers.Add(sub);
        }

        try
        {
            long last = afterSeq;
            foreach (var line in backlog)
            {
                last = line.Seq;
                yield return line;
            }

            await foreach (var line in sub.Reader.ReadAllAsync(ct))
            {
                if (line.Seq <= last) continue;
                last = line.Seq;
                yield return line;
            }
        }
        finally
        {
            lock (_gate) _subscribers.Remove(sub);
        }
    }

    /// <summary>Ends every live subscription. The scrollback stays readable.</summary>
    public void Complete()
    {
        Channel<ChessLabTerminalLine>[] subscribers;
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            subscribers = [.. _subscribers];
        }
        foreach (var sub in subscribers) sub.Writer.TryComplete();
    }

    private List<ChessLabTerminalLine> SnapshotLocked(long afterSeq)
    {
        var list = new List<ChessLabTerminalLine>(_count);
        for (int i = 0; i < _count; i++)
        {
            var line = _ring[(_start + i) % _ring.Length];
            if (line.Seq > afterSeq) list.Add(line);
        }
        return list;
    }
}
