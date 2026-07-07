using Laplace.Engine.Core;
using Laplace.Modality;

namespace Laplace.Chess.Service;

/// <summary>
/// Lab self-play → per-ply substrate evidence via shared <see cref="ChessLiveGameHost"/>.
/// </summary>
public sealed class ChessLabRecorder : IAsyncDisposable
{
    private readonly ChessLiveGameHost _host;
    private readonly string _learnContext;
    private readonly bool _ownsHost;

    public long GamesRecorded { get; private set; }

    public ChessLiveGameHost Host => _host;

    private ChessLabRecorder(ChessLiveGameHost host, string learnContext, bool ownsHost)
    {
        _host = host;
        _learnContext = learnContext;
        _ownsHost = ownsHost;
    }

    public static async Task<ChessLabRecorder> OpenAsync(
        string learnContext, double witnessWeight = 0.5d, CancellationToken ct = default)
    {
        var host = await ChessLiveGameHost.CreateAsync(witnessWeight, learnContext, ct);
        return new ChessLabRecorder(host, learnContext, ownsHost: true);
    }

    public static ChessLabRecorder Attach(ChessLiveGameHost host, string learnContext)
        => new(host, learnContext, ownsHost: false);

    public Task RecordGameAsync(
        IReadOnlyList<RecordedEdge> edges, bool adjudicated, CancellationToken ct = default)
    {
        if (edges.Count == 0) return Task.CompletedTask;
        GamesRecorded++;
        return _host.LearnGameAsync(edges, _learnContext, adjudicated, ct);
    }

    public void RecordGameBlocking(IReadOnlyList<RecordedEdge> edges, bool adjudicated)
        => RecordGameAsync(edges, adjudicated, CancellationToken.None).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_ownsHost)
            await _host.DisposeAsync();
    }
}
