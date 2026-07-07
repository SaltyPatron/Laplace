using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Lab self-play → substrate evidence: each game becomes move/position attestations + consensus fold,
/// same path as <see cref="ChessEngineService"/> training (SubstrateTurnHost.LearnGameAsync).
/// </summary>
public sealed class ChessLabRecorder : IAsyncDisposable
{
    private readonly NpgsqlDataSource _ds;
    private readonly ConsensusAccumulatingWriter _writer;
    private readonly SubstrateTurnHost _host;
    private readonly SemaphoreSlim _learnGate = new(1, 1);

    public long GamesRecorded { get; private set; }

    private ChessLabRecorder(
        NpgsqlDataSource ds, ConsensusAccumulatingWriter writer, SubstrateTurnHost host)
    {
        _ds = ds;
        _writer = writer;
        _host = host;
    }

    public static async Task<ChessLabRecorder> OpenAsync(
        string learnContext, double witnessWeight = 0.5d, CancellationToken ct = default)
    {
        CodepointPerfcache.LoadDefault();
        var conn = ChessEngineService.ResolveConnString();
        var ds = new NpgsqlDataSourceBuilder(conn).Build();
        var inner = new NpgsqlSubstrateWriter(ds);
        var writer = new ConsensusAccumulatingWriter(
            inner, ds, foldWorkers: 1, freshSource: false, persistEvidence: true, stageAsWalks: false);
        var reader = new NpgsqlSubstrateReader(ds);
        var host = new SubstrateTurnHost(ds, writer, reader, witnessWeight, learnContext);
        var canonicalNames = await ChessVocabulary.BootstrapAsync(writer, ct);
        await RegisterCanonicalsAsync(ds, canonicalNames, ct);
        return new ChessLabRecorder(ds, writer, host);
    }

    public async Task RecordGameAsync(
        IReadOnlyList<RecordedEdge> edges, bool adjudicated, CancellationToken ct = default)
    {
        if (edges.Count == 0) return;
        await _learnGate.WaitAsync(ct);
        try
        {
            await _host.LearnGameAsync(edges, adjudicated, ct);
            GamesRecorded++;
        }
        finally
        {
            _learnGate.Release();
        }
    }

    public void RecordGameBlocking(IReadOnlyList<RecordedEdge> edges, bool adjudicated)
        => RecordGameAsync(edges, adjudicated, CancellationToken.None).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        _learnGate.Dispose();
        await _writer.DisposeAsync();
        await _ds.DisposeAsync();
    }

    private static async Task RegisterCanonicalsAsync(
        NpgsqlDataSource ds, IReadOnlyCollection<string> names, CancellationToken ct)
    {
        if (names.Count == 0) return;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT laplace.register_canonicals(@names)";
        cmd.Parameters.Add(new NpgsqlParameter
        {
            ParameterName = "names",
            Value = names.ToArray(),
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
        });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
