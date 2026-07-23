using Laplace.Api.Contracts;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The live scoreboard + modality reads. Thin callers over the installed
/// substrate_pulse() / modality_counts() — the set logic lives in the
/// extension (one implementation; the MCP server reads the same functions),
/// C# only maps rows.
/// </summary>
internal sealed partial class SubstrateClient
{
    public async Task<ModalitiesResponse> ModalitiesAsync(CancellationToken ct)
    {
        const string sql = "SELECT text_evidence, chess, models, multilingual FROM laplace.modality_counts()";
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = 20;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return new ModalitiesResponse("modalities", 0, 0, 0, 0);
            return new ModalitiesResponse("modalities",
                r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate is unreachable.", ex);
        }
    }

    public async Task<PulseResponse> PulseAsync(long nowUnix, CancellationToken ct)
    {
        const string sql = """
            SELECT entities, attestations, consensus, physicalities,
                   extract(epoch FROM last_flush_at)::bigint, flushes_last_min, folding
            FROM laplace.substrate_pulse()
            """;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return new PulseResponse("pulse", nowUnix, 0, 0, 0, 0, null, 0, false);
            return new PulseResponse("pulse", nowUnix,
                r.IsDBNull(0) ? 0 : r.GetInt64(0),
                r.IsDBNull(1) ? 0 : r.GetInt64(1),
                r.IsDBNull(2) ? 0 : r.GetInt64(2),
                r.IsDBNull(3) ? 0 : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetInt64(4),
                r.IsDBNull(5) ? 0 : r.GetInt64(5),
                !r.IsDBNull(6) && r.GetBoolean(6));
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate is unreachable.", ex);
        }
    }
}
