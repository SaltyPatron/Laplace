using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace Laplace.Chess.Service.Uci;

/// <summary>Why an engine operation failed. Callers map these to their own errors; none is ever answered by another engine.</summary>
public enum UciFailure { Unavailable, Busy, Timeout, Protocol, Rejected }

public sealed class UciEngineException(UciFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public UciFailure Failure { get; } = failure;
}

/// <summary>How to start an engine process: the executable, its arguments, extra environment, and the network file it
/// loads (named so its bytes are receipted). The working directory defaults to the executable's.</summary>
public sealed record UciProcessStart(
    string ExePath,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? NetworkPath = null,
    TimeSpan? HandshakeTimeout = null);

/// <summary>What answered: the executable by path and SHA-256, its UCI <c>id</c>, its arguments, and the network it was
/// given. <see cref="Via"/> is the engine a front (laplace-uci's proxy) reported it runs, from its receipt line.</summary>
public sealed record EngineIdentity(
    string ExePath, string ExeSha256, string Name, string? Author, IReadOnlyList<string> Arguments,
    string? NetworkPath = null, string? NetworkSha256 = null, EngineIdentity? Via = null);

/// <summary>A job's receipt: which engine, on which host, under which options and state, for which position and
/// limits, and what it cost. EngineMs is go to bestmove; QueueMs and WallMs, when a caller sets them, are its own time
/// around that (pool wait, spawn, the network), kept apart so they are never counted as engine time.</summary>
public sealed record EngineReceipt(
    string Host, EngineIdentity Engine, IReadOnlyList<KeyValuePair<string, string>> Options,
    ChessPositionKey Position, string Go, string StateMode, long EngineMs, long? Nodes,
    long? OverheadMs = null, long? QueueMs = null, long? WallMs = null)
{
    /// <summary>The receipt for a plain UCI consumer: one <c>info string</c> line.</summary>
    public string EncodeInfoString() => "info string " + ReceiptPrefix + JsonSerializer.Serialize(this, UciJson.Options);
    public const string ReceiptPrefix = "laplace receipt ";
    public const string IdentityPrefix = "laplace engine ";
}

/// <summary>One search's result in the shared typed model: position-keyed, carrying the provider's identity in its receipt.
/// Lines holds the last complete line per multipv rank; Final is the last scored line the engine sent.</summary>
public sealed record EngineAnalysis(
    ChessPositionKey Position, EngineBestMove BestMove, IReadOnlyList<EngineInfo> Lines, EngineInfo? Final,
    IReadOnlyList<string> Strings, EngineReceipt Receipt);

public static class UciJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new UciMoveJsonConverter(), new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class UciMoveJsonConverter : System.Text.Json.Serialization.JsonConverter<UciMove>
    {
        public override UciMove Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => UciMove.Parse(reader.GetString() ?? "");
        public override void Write(Utf8JsonWriter writer, UciMove value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Text);
    }
}

/// <summary>
/// One UCI engine process: the generic client every surface uses. Start performs the uci/uciok handshake; options
/// are validated against what the engine advertises; every read has a deadline; a timeout, a dead process or a
/// protocol violation marks it Broken, and Dispose sends quit and then kills the process tree. Not thread-safe:
/// one caller drives one process (pools hand out whole processes).
/// </summary>
public sealed class UciProcess : IDisposable
{
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), string> HashCache = new();
    private readonly Process? _proc;
    private readonly TextWriter _stdin;
    private readonly TextReader _stdout;
    private volatile bool _ended;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly Dictionary<string, UciOptionInfo> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<KeyValuePair<string, string>> _sent = [];
    private readonly List<string> _handshake = [];
    private bool _broken;
    private int _disposed;
    private Task? _relay;

    public EngineIdentity Identity { get; private set; }
    public IReadOnlyDictionary<string, UciOptionInfo> Options => _options;
    /// <summary>The engine's own handshake lines (id and option), as a front replays them.</summary>
    public IReadOnlyList<string> HandshakeLines => _handshake;
    public IReadOnlyList<KeyValuePair<string, string>> OptionsSent => _sent;
    public bool Broken => _broken || _ended || (_proc?.HasExited ?? false);

    private UciProcess(Process? proc, TextWriter stdin, TextReader stdout, EngineIdentity identity)
    {
        _proc = proc;
        _stdin = stdin;
        _stdout = stdout;
        Identity = identity;
    }

    /// <summary>A client over streams instead of a process (tests drive a scripted engine this way).</summary>
    internal static UciProcess Attach(TextWriter toEngine, TextReader fromEngine, TimeSpan handshakeTimeout)
    {
        var client = new UciProcess(null, toEngine, fromEngine, new EngineIdentity("(attached)", "", "", null, []));
        _ = client.PumpAsync();
        try { client.Handshake("(attached)", handshakeTimeout); return client; }
        catch { client.Dispose(); throw; }
    }

    private void Handshake(string exe, TimeSpan timeout)
    {
        string? name = null, author = null;
        Send("uci");
        WaitFor("uciok", timeout, line =>
        {
            if (line.StartsWith("id name ", StringComparison.Ordinal)) { name = line[8..].Trim(); _handshake.Add(line); }
            else if (line.StartsWith("id author ", StringComparison.Ordinal)) { author = line[10..].Trim(); _handshake.Add(line); }
            else if (UciOptionInfo.Decode(line) is { } option) { _options[option.Name] = option; _handshake.Add(line); }
        });
        if (string.IsNullOrWhiteSpace(name))
            throw new UciEngineException(UciFailure.Protocol, $"{exe} did not report its UCI engine name.");
        Identity = Identity with { Name = name, Author = author };
    }

    public static UciProcess Start(UciProcessStart start)
    {
        if (string.IsNullOrWhiteSpace(start.ExePath) || !File.Exists(start.ExePath))
            throw new UciEngineException(UciFailure.Unavailable, $"engine executable not found: {start.ExePath}");
        string exe = Path.GetFullPath(start.ExePath);
        string sha = FileSha256(exe);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in start.Arguments ?? []) psi.ArgumentList.Add(a);
        foreach (var (k, v) in start.Environment ?? new Dictionary<string, string?>()) psi.Environment[k] = v;
        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null"); }
        catch (Exception ex) when (ex is not UciEngineException)
        {
            throw new UciEngineException(UciFailure.Unavailable, $"engine failed to start: {exe}: {ex.Message}", ex);
        }
        var client = new UciProcess(proc, proc.StandardInput, proc.StandardOutput, new EngineIdentity(exe, sha, "", null, start.Arguments ?? [],
            start.NetworkPath is { Length: > 0 } net ? Path.GetFullPath(net) : null,
            start.NetworkPath is { Length: > 0 } n && File.Exists(n) ? FileSha256(n) : null));
        // An unread redirected stderr can fill its pipe and block the engine before it writes bestmove.
        proc.ErrorDataReceived += static (_, _) => { };
        proc.BeginErrorReadLine();
        _ = client.PumpAsync();
        try
        {
            client.Handshake(exe, start.HandshakeTimeout ?? TimeSpan.FromSeconds(30));
            if (File.Exists(exe) && FileSha256(exe) != sha)
                throw new UciEngineException(UciFailure.Protocol, "engine executable changed during the handshake");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static string FileSha256(string path)
    {
        var info = new FileInfo(path);
        return HashCache.GetOrAdd((info.FullName, info.Length, info.LastWriteTimeUtc), static key =>
        {
            using var stream = File.OpenRead(key.Path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        });
    }

    public bool Supports(string option) => _options.ContainsKey(option);

    /// <summary>Send <c>setoption</c>, validated against the advertised option; the value is receipted.</summary>
    public void SetOption(string name, string value)
    {
        if (!_options.TryGetValue(name, out var option))
            throw new UciEngineException(UciFailure.Rejected, $"{Identity.Name} has no option '{name}'.");
        try { option.Validate(value); }
        catch (ArgumentException ex) { throw new UciEngineException(UciFailure.Rejected, ex.Message, ex); }
        Send(option.Type == "button" ? $"setoption name {option.Name}" : $"setoption name {option.Name} value {value}");
        _sent.RemoveAll(p => p.Key.Equals(option.Name, StringComparison.OrdinalIgnoreCase));
        _sent.Add(new(option.Name, value));
        // The network an engine loads is part of what answered: Lc0's WeightsFile, Stockfish's EvalFile.
        if (option.Name is "WeightsFile" or "EvalFile" && Path.IsPathRooted(value) && File.Exists(value))
            Identity = Identity with { NetworkPath = Path.GetFullPath(value), NetworkSha256 = FileSha256(value) };
    }

    /// <summary>isready/readyok. A receipt or identity line a front sends before readyok is decoded into Identity.Via;
    /// an option rejection the engine reports is a Rejected failure.</summary>
    public void IsReady(TimeSpan? timeout = null, Action<string>? observe = null)
    {
        Send("isready");
        WaitFor("readyok", timeout ?? TimeSpan.FromSeconds(60), line =>
        {
            ObserveFront(line);
            if (line.Contains("invalid value", StringComparison.OrdinalIgnoreCase)
                || line.Contains("No such option", StringComparison.OrdinalIgnoreCase))
                throw new UciEngineException(UciFailure.Rejected, $"{Identity.Name} rejected its configuration: {line}");
            observe?.Invoke(line);
        });
    }

    /// <summary>Fresh engine state for an independent job: ucinewgame, the hash cleared where the engine has the button.</summary>
    public void NewGame(TimeSpan? timeout = null)
    {
        Send("ucinewgame");
        if (_options.TryGetValue("Clear Hash", out var clear) && clear.Type == "button") Send("setoption name Clear Hash");
        IsReady(timeout);
    }

    /// <summary>Search one position. On the deadline the engine is told stop and given a short grace for its bestmove;
    /// without one it is Broken. Cancellation is stop, and the bestmove the engine then sends is the result.</summary>
    public EngineAnalysis Search(ChessPosition position, UciLimits limits, TimeSpan timeout, string stateMode,
        IChessIdentityResolver? resolver = null, Action<EngineInfo>? onInfo = null, CancellationToken ct = default)
    {
        if (Broken) throw new UciEngineException(UciFailure.Unavailable, $"{Identity.Name} is not running.");
        if (limits.Infinite && !ct.CanBeCanceled)
            throw new ArgumentException("go infinite needs a cancellation token to stop it.");
        bool white = position.WhiteToMove;
        string go = limits.Encode();
        Send(position.Encode());
        var sw = Stopwatch.StartNew();
        Send(go);
        var byRank = new SortedDictionary<int, EngineInfo>();
        var strings = new List<string>();
        EngineInfo? final = null;
        long? nodes = null;
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        bool stopped = false;
        while (true)
        {
            string? line;
            try
            {
                using var reg = ct.Register(() => { if (!stopped) { stopped = true; TrySend("stop"); } });
                line = ReadLine(deadline);
            }
            catch (TimeoutException) when (!stopped)
            {
                stopped = true;
                TrySend("stop");
                deadline = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
                try { while (ReadLine(deadline) is { } l && !l.StartsWith("bestmove", StringComparison.Ordinal)) { } }
                catch (TimeoutException) { _broken = true; }
                throw new UciEngineException(UciFailure.Timeout, $"{Identity.Name} gave no bestmove within {timeout.TotalSeconds:0.#}s");
            }
            catch (TimeoutException)
            {
                _broken = true;
                throw new UciEngineException(UciFailure.Timeout, $"{Identity.Name} did not answer stop");
            }
            if (line is null)
            {
                _broken = true;
                throw new UciEngineException(UciFailure.Unavailable, $"{Identity.Name} exited during search");
            }
            if (EngineBestMove.Decode(line) is { } best)
            {
                sw.Stop();
                var receipt = new EngineReceipt(System.Environment.MachineName, Identity, _sent.ToList(),
                    (resolver ?? NotationIdentityResolver.Instance).ResolvePosition(position), go, stateMode,
                    sw.ElapsedMilliseconds, nodes);
                return new EngineAnalysis(receipt.Position, best, byRank.Values.ToList(), final, strings, receipt);
            }
            if (EngineInfo.Decode(line, white) is not { } info) continue;
            if (info.Text is { } text)
            {
                if (!ObserveFront(line)) strings.Add(text);
                continue;
            }
            if (info.Nodes is { } n) nodes = n;
            if (info.Score is not null)
            {
                final = info;
                byRank[info.MultiPv ?? 1] = info;
            }
            onInfo?.Invoke(info);
        }
    }

    /// <summary>Raw protocol, for a front that relays a GUI's commands.</summary>
    public void SendRaw(string line) => Send(line);

    /// <summary>Hand every further output line to <paramref name="sink"/> (a front relaying to its GUI). After this the
    /// typed calls are not available on this process.</summary>
    public Task RelayOutput(Action<string> sink)
    {
        _relay ??= Task.Run(async () =>
        {
            await foreach (var line in _lines.Reader.ReadAllAsync()) sink(line);
        });
        return _relay;
    }

    public bool WaitForExit(TimeSpan timeout) => _proc?.WaitForExit(timeout) ?? true;

    /// <summary>Send quit and wait for the engine to exit; its exit code (0 for an attached client), or null when it did
    /// not exit in time (Dispose then kills it).</summary>
    public int? Quit(TimeSpan timeout)
    {
        TrySend("quit");
        _ended = true;
        if (_proc is null) return 0;
        return _proc.WaitForExit(timeout) ? _proc.ExitCode : null;
    }

    private bool ObserveFront(string line)
    {
        const string head = "info string ";
        if (!line.StartsWith(head, StringComparison.Ordinal)) return false;
        string body = line[head.Length..];
        try
        {
            if (body.StartsWith(EngineReceipt.IdentityPrefix, StringComparison.Ordinal))
            {
                var via = JsonSerializer.Deserialize<EngineIdentity>(body[EngineReceipt.IdentityPrefix.Length..], UciJson.Options);
                if (via is not null) Identity = Identity with { Via = via };
                return true;
            }
            return body.StartsWith(EngineReceipt.ReceiptPrefix, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await _stdout.ReadLineAsync().ConfigureAwait(false) is { } line)
                _lines.Writer.TryWrite(line);
        }
        catch { }
        finally { _ended = true; _lines.Writer.TryComplete(); }
    }

    private void Send(string cmd)
    {
        if (_relay is null && Broken && cmd != "quit")
            throw new UciEngineException(UciFailure.Unavailable, $"{(Identity.Name.Length > 0 ? Identity.Name : Identity.ExePath)} is not running.");
        try
        {
            _stdin.WriteLine(cmd);
            _stdin.Flush();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _broken = true;
            throw new UciEngineException(UciFailure.Unavailable, "engine input closed", ex);
        }
    }

    private void TrySend(string cmd) { try { Send(cmd); } catch { } }

    private void WaitFor(string marker, TimeSpan timeout, Action<string>? observe = null)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            string? line;
            try { line = ReadLine(deadline); }
            catch (TimeoutException)
            {
                _broken = true;
                throw new UciEngineException(UciFailure.Timeout, $"{(Identity.Name.Length > 0 ? Identity.Name : Identity.ExePath)} never answered '{marker}' within {timeout.TotalSeconds:0.#}s");
            }
            if (line is null)
            {
                _broken = true;
                throw new UciEngineException(UciFailure.Unavailable, $"{(Identity.Name.Length > 0 ? Identity.Name : Identity.ExePath)} exited before '{marker}'");
            }
            observe?.Invoke(line);
            if (line.StartsWith(marker, StringComparison.Ordinal)) return;
        }
    }

    private string? ReadLine(long deadline)
    {
        if (_relay is not null) throw new InvalidOperationException("this process relays its output");
        long remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0) throw new TimeoutException();
        var wait = _lines.Reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(remaining / (double)Stopwatch.Frequency));
        try
        {
            if (!wait.GetAwaiter().GetResult()) return null;
        }
        catch (TimeoutException) { throw; }
        return _lines.Reader.TryRead(out var line) ? line : ReadLine(deadline);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (!Broken) { _stdin.WriteLine("quit"); _stdin.Flush(); } } catch { }
        if (_proc is null) { try { _stdin.Dispose(); } catch { } return; }
        try
        {
            if (!_proc.HasExited && !_proc.WaitForExit(1000))
                _proc.Kill(entireProcessTree: true);
        }
        catch { }
        _proc.Dispose();
    }
}
