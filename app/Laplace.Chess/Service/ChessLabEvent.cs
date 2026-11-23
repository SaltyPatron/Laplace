using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Laplace.Chess.Service;

public abstract record ChessLabEvent
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ChessLabLogEvent(string Level, string Message) : ChessLabEvent;

public sealed record ChessLabProgressEvent(int Done, int Total, string? Label = null) : ChessLabEvent;

public sealed record ChessLabGameEvent(
    int Index,
    string? White,
    string? Black,
    string Result,
    string? PgnPath = null) : ChessLabEvent;

public sealed record ChessLabMetricEvent(string Name, double Value, string? Unit = null) : ChessLabEvent;

/// <summary>
/// One ply of a live game, enough to draw a board: emitted by the cutechess
/// runner (parsed from -debug UCI traffic) and by in-process self-play (via the
/// MatchRunner onPly tap). Game numbers disambiguate interleaved parallel games.
/// </summary>
public sealed record ChessLabBoardEvent(
    int Game,
    int Ply,
    string Uci,
    string Fen,
    string? White = null,
    string? Black = null) : ChessLabEvent;

public sealed record ChessLabTableEvent(
    string Title,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    ChessLabTableAction? Action = null) : ChessLabEvent;

/// <summary>A deterministic action derived from one table cell, rendered as a button.</summary>
public sealed record ChessLabTableAction(
    string Label,
    string Kind,
    string ConfigKey,
    int ValueColumn);

public sealed record ChessLabDoneEvent(ChessLabJobState FinalState, string? Message = null) : ChessLabEvent;

/// <summary>
/// The exact process a job is about to launch, published before it starts. The viewer
/// renders it as a prompt line and lets the operator copy it, so "what did the lab
/// actually run?" is answerable from the UI instead of only from a server-side log.
/// </summary>
public sealed record ChessLabCommandEvent(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null) : ChessLabEvent
{
    /// <summary>The command as a single copy-pasteable line, POSIX-quoted where needed.</summary>
    public string CommandLine => string.Join(' ', new[] { FileName }.Concat(Arguments).Select(Quote));

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"', '\'', '\\', '$', '&', '|', ';', '<', '>']) < 0
            ? arg
            : '"' + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';
}

/// <summary>
/// One line of raw process I/O. Routed to the job's <see cref="ChessLabTerminal"/> rather
/// than the structured event channel — see that type for why the two cannot share one.
/// </summary>
public sealed record ChessLabTerminalEvent(
    string Stream,
    string Text,
    string? Engine = null,
    string? Direction = null,
    int? EngineInstance = null) : ChessLabEvent;

public sealed record ChessLabSequencedEvent(long Seq, ChessLabEvent Event);

/// <summary>
/// Multi-subscriber sequenced lab event stream with a bounded ring buffer for replay
/// (via <c>Last-Event-ID</c> or <c>?after=</c>) and independent per-subscriber live channels.
/// </summary>
public sealed class ChessLabEventStream
{
    public const int Capacity = 4096;

    private readonly object _gate = new();
    private readonly ChessLabSequencedEvent[] _ring = new ChessLabSequencedEvent[Capacity];
    private readonly List<Subscriber> _subscribers = new();
    private long _nextSeq = 1;
    private int _count;
    private bool _completed;

    public int RingCount { get { lock (_gate) return _count; } }
    public bool IsCompleted { get { lock (_gate) return _completed; } }

    public long Publish(ChessLabEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ChessLabSequencedEvent item;
        Subscriber[] subs;
        lock (_gate)
        {
            if (_completed) return -1;
            long seq = _nextSeq++;
            item = new ChessLabSequencedEvent(seq, evt);
            _ring[(int)((seq - 1) % Capacity)] = item;
            if (_count < Capacity) _count++;
            subs = _subscribers.ToArray();
        }

        foreach (var sub in subs)
            sub.Channel.Writer.TryWrite(item);

        return item.Seq;
    }

    public void Complete()
    {
        Subscriber[] subs;
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            subs = _subscribers.ToArray();
        }

        foreach (var sub in subs)
            sub.Channel.Writer.TryComplete();
    }

    public IReadOnlyList<ChessLabSequencedEvent> Snapshot(long afterSeq = 0)
    {
        lock (_gate)
            return CollectReplayLocked(afterSeq);
    }

    public ChannelReader<ChessLabEvent> SubscribeReader(long afterSeq = 0)
    {
        var channel = Channel.CreateUnbounded<ChessLabEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _ = PumpReaderAsync(channel.Writer, afterSeq);
        return channel.Reader;
    }

    private async Task PumpReaderAsync(ChannelWriter<ChessLabEvent> writer, long afterSeq)
    {
        try
        {
            await foreach (var item in ReadAsync(afterSeq, CancellationToken.None).ConfigureAwait(false))
            {
                if (!writer.TryWrite(item.Event))
                    await writer.WriteAsync(item.Event).ConfigureAwait(false);
            }
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }

    public async IAsyncEnumerable<ChessLabSequencedEvent> ReadAsync(
        long afterSeq = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        IReadOnlyList<ChessLabSequencedEvent> replay;
        Subscriber? sub = null;
        long lastDelivered = afterSeq;

        lock (_gate)
        {
            replay = CollectReplayLocked(afterSeq);
            if (!_completed)
            {
                sub = new Subscriber(Channel.CreateBounded<ChessLabSequencedEvent>(
                    new BoundedChannelOptions(Capacity)
                    {
                        SingleReader = true,
                        SingleWriter = false,
                        FullMode = BoundedChannelFullMode.DropOldest,
                    }));
                _subscribers.Add(sub);
            }
        }

        try
        {
            foreach (var item in replay)
            {
                ct.ThrowIfCancellationRequested();
                if (item.Seq > lastDelivered)
                {
                    lastDelivered = item.Seq;
                    yield return item;
                }
            }

            if (sub is null) yield break;

            await foreach (var live in sub.Channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (live.Seq > lastDelivered)
                {
                    lastDelivered = live.Seq;
                    yield return live;
                }
            }
        }
        finally
        {
            if (sub is not null)
            {
                lock (_gate) { _subscribers.Remove(sub); }
            }
        }
    }

    private List<ChessLabSequencedEvent> CollectReplayLocked(long afterSeq)
    {
        var items = new List<ChessLabSequencedEvent>(_count);
        if (_count == 0) return items;
        long oldestSeq = _nextSeq - _count;
        long firstSeq = Math.Max(oldestSeq, afterSeq + 1);
        for (long s = firstSeq; s < _nextSeq; s++)
        {
            var item = _ring[(int)((s - 1) % Capacity)];
            if (item is not null && item.Seq > afterSeq)
                items.Add(item);
        }
        return items;
    }

    private sealed record Subscriber(Channel<ChessLabSequencedEvent> Channel);
}
