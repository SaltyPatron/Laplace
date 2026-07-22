using Laplace.Api.Contracts;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The live scoreboard read. A thin composition of two cheap existing surfaces —
/// substrate_counts() (STABLE estimate) and a windowed recency query over
/// ingest_flush_journal — so it can be polled every few seconds. No fold-math
/// here; if it earns a regress pin it graduates to an installed substrate_pulse().
/// </summary>
internal sealed partial class SubstrateClient
{
    public async Task<PulseResponse> PulseAsync(long nowUnix, CancellationToken ct)
    {
        // One round trip: the estimate counts, then the heartbeat. Both are
        // sub-second; substrate_counts() is a catalog estimate, the flush query
        // is a PK-range scan of a 51-row journal.
        const string countsSql = "SELECT metric, value FROM laplace.substrate_counts()";
        // Folding is true when rows are landing OR a fold/apply backend is
        // active — the flush journal alone under-reports a slow-flush ingest
        // that is mid-working-set. Both signals are cheap: a PK scan of a tiny
        // journal, and a scan of pg_stat_activity for this database's writers.
        const string flushSql = """
            SELECT extract(epoch FROM max(j.applied_at))::bigint AS last_flush,
                   count(*) FILTER (WHERE j.applied_at > now() - interval '60 seconds') AS last_min,
                   (max(j.applied_at) > now() - interval '20 seconds')
                     OR EXISTS (
                       SELECT 1 FROM pg_stat_activity a
                       WHERE a.datname = current_database()
                         AND a.state = 'active'
                         -- Exclude this very query: its own text contains the
                         -- literal pattern strings below, so without this it
                         -- matches itself and reports folding forever.
                         AND a.pid <> pg_backend_pid()
                         AND a.query ILIKE ANY (ARRAY['%attestation_merge%', '%consensus_upsert%', '%COPY %laplace.%'])
                     ) AS folding
            FROM laplace.ingest_flush_journal j
            """;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            long entities = 0, attestations = 0, consensus = 0, physicalities = 0;
            await using (var cmd = new NpgsqlCommand(countsSql, conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    var metric = r.GetString(0);
                    var value = r.GetInt64(1);
                    if (metric.StartsWith("entities", StringComparison.Ordinal)) entities = value;
                    else if (metric.StartsWith("attestations", StringComparison.Ordinal)) attestations = value;
                    else if (metric.StartsWith("consensus", StringComparison.Ordinal)) consensus = value;
                    else if (metric.StartsWith("physicalities", StringComparison.Ordinal)) physicalities = value;
                }
            }

            long? lastFlush = null;
            long lastMin = 0;
            bool folding = false;
            await using (var cmd = new NpgsqlCommand(flushSql, conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (await r.ReadAsync(ct))
                {
                    lastFlush = r.IsDBNull(0) ? null : r.GetInt64(0);
                    lastMin = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                    folding = !r.IsDBNull(2) && r.GetBoolean(2);
                }
            }

            return new PulseResponse("pulse", nowUnix, entities, attestations, consensus,
                physicalities, lastFlush, lastMin, folding);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate is unreachable.", ex);
        }
    }
}
