using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed partial class NpgsqlSubstrateWriter
{
    private static async Task<bool> RelationHasRowsAsync(
        NpgsqlDataSource ds, string relation, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return await RelationHasRowsAsync(conn, null, relation, ct);
    }

    private static async Task<bool> RelationHasRowsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string relation, CancellationToken ct)
    {
        string queryName = relation switch
        {
            "entities" => "write.entities_have_rows",
            "physicalities" => "write.physicalities_have_rows",
            "attestations" => "write.attestations_have_rows",
            _ => throw new ArgumentOutOfRangeException(nameof(relation)),
        };
        await using var cmd = NpgsqlCatalog.Command(conn, tx, queryName);
        var o = await cmd.ExecuteScalarAsync(ct);
        return o is true;
    }

    private static async Task<HashSet<Hash128>> ProbePresentControlAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        string queryName, IReadOnlyList<Hash128> ids,
        Action<NpgsqlParameterCollection, int, int> bindKeys,
        Action<int> addRoundTrips, CancellationToken ct)
    {
        var present = new HashSet<Hash128>();
        if (ids.Count == 0) return present;

        int probeChunkIds = ApplySizing.ProbeChunkIds;
        int chunkCount = (ids.Count + probeChunkIds - 1) / probeChunkIds;
        for (int c = 0; c < chunkCount; c++)
        {
            int start = c * probeChunkIds;
            int n = Math.Min(probeChunkIds, ids.Count - start);
            var chunk = new byte[n][];
            for (int i = 0; i < n; i++) chunk[i] = ids[start + i].ToBytes();

            await using var cmd = NpgsqlCatalog.Command(conn, tx, SqlCatalog.Get(queryName), parameters =>
            {
                parameters.Add(new NpgsqlParameter
                { Value = chunk, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                bindKeys(parameters, start, n);
            });
            cmd.CommandTimeout = 0;
            await cmd.PrepareAsync(ct);
            var bm = await cmd.ExecuteScalarAsync(ct) as byte[] ?? Array.Empty<byte>();

            long bits = (long)bm.Length * 8;
            for (int i = 0; i < n; i++)
                if (i < bits && (bm[i >> 3] & (1 << (i & 7))) != 0)
                    present.Add(ids[start + i]);
        }
        addRoundTrips(chunkCount);
        return present;
    }

    private static Task<HashSet<Hash128>> ProbePresentKeyedControlAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        string queryName, IReadOnlyList<Hash128> ids, IReadOnlyList<Hash128> typeIds,
        IReadOnlyList<Hash128> subjectIds, Action<int> addRoundTrips, CancellationToken ct)
    {
        if (typeIds.Count != ids.Count || subjectIds.Count != ids.Count)
            throw new InvalidOperationException(
                $"keyed probe arrays misaligned: {ids.Count} ids / {typeIds.Count} types / {subjectIds.Count} subjects");
        return ProbePresentControlAsync(conn, tx, queryName, ids,
            (parameters, start, n) =>
            {
                var chunkTypes = new byte[n][];
                var chunkSubjects = new byte[n][];
                for (int i = 0; i < n; i++)
                {
                    chunkTypes[i] = typeIds[start + i].ToBytes();
                    chunkSubjects[i] = subjectIds[start + i].ToBytes();
                }
                parameters.Add(new NpgsqlParameter
                { Value = chunkTypes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                parameters.Add(new NpgsqlParameter
                { Value = chunkSubjects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            }, addRoundTrips, ct);
    }

    /// <summary>
    /// Shared chunked, connection-parallel presence probe. Sends the ids in
    /// sized chunks as $1 (bytea[]); <paramref name="bindKeys"/> adds the target
    /// table's partition-key arrays for the same [start, start+n) window. The
    /// returned bitmap is decoded back to the ids found present. Every probe
    /// shape (id-only and keyed) uses this one implementation.
    /// </summary>
    private async Task<HashSet<Hash128>> ProbePresentCoreAsync(
        string queryName, IReadOnlyList<Hash128> ids,
        Action<NpgsqlParameterCollection, int, int> bindKeys,
        Action<int> addRoundTrips, CancellationToken ct)
    {
        var present = new HashSet<Hash128>();
        if (ids.Count == 0) return present;

        // Fill a bounded request before opening another connection. Splitting a
        // small set by CPU count multiplies network requests without needing more
        // memory. Sets exceeding the transit budget still use bounded parallelism.
        int probeChunkIds = ApplySizing.ProbeChunkIds;
        int chunkCount = (ids.Count + probeChunkIds - 1) / probeChunkIds;
        var perChunk = new List<Hash128>[chunkCount];

        async Task ProbeChunkAsync(int c, CancellationToken token)
        {
            int start = c * probeChunkIds;
            int n = Math.Min(probeChunkIds, ids.Count - start);
            var chunk = new byte[n][];
            for (int i = 0; i < n; i++) chunk[i] = ids[start + i].ToBytes();

            await using var conn = await _ds.OpenConnectionAsync(token);
            await using var cmd = NpgsqlCatalog.Command(conn, null, SqlCatalog.Get(queryName), parameters =>
            {
                parameters.Add(new NpgsqlParameter
                { Value = chunk, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                bindKeys(parameters, start, n);
            });
            cmd.CommandTimeout = 0;
            await cmd.PrepareAsync(token);
            var bm = await cmd.ExecuteScalarAsync(token) as byte[] ?? Array.Empty<byte>();

            var hits = new List<Hash128>();
            long bits = (long)bm.Length * 8;
            for (int i = 0; i < n; i++)
                if (i < bits && (bm[i >> 3] & (1 << (i & 7))) != 0)
                    hits.Add(ids[start + i]);
            perChunk[c] = hits;
        }

        if (chunkCount == 1)
        {
            await CopyConnectionBudget.WaitAsync(ct);
            try
            {
                await ProbeChunkAsync(0, ct);
            }
            finally
            {
                CopyConnectionBudget.Release();
            }
        }
        else
        {
            // Chunk workers are bounded by ApplyParallelism, the datasource pool limit,
            // and CopyConnectionBudget so concurrent entity/physicality/attestation probes
            // cannot exhaust the pool.
            int workerLimit = Math.Max(1, ResolveWorkerConnectionLimit(_ds));
            int workers = Math.Min(chunkCount, Math.Min(ApplyParallelism, workerLimit));
            int next = -1;
            await CpuTopology.RunPinnedAsyncParallel(workers, async (_, token) =>
            {
                for (int c = Interlocked.Increment(ref next); c < chunkCount;
                     c = Interlocked.Increment(ref next))
                {
                    await CopyConnectionBudget.WaitAsync(token);
                    try
                    {
                        await ProbeChunkAsync(c, token);
                    }
                    finally
                    {
                        CopyConnectionBudget.Release();
                    }
                }
            }, ct);
        }

        foreach (var hits in perChunk)
            if (hits is not null)
                foreach (var id in hits) present.Add(id);
        addRoundTrips(chunkCount);
        return present;
    }

    // Identity permutation sorted by a partition-key comparison over indices, so all parallel
    // probe arrays can be reordered together for forward index walks (see the call site).
    private static int[] BuildProbePermutation(int count, Comparison<int> byKey)
    {
        var perm = new int[count];
        for (int i = 0; i < count; i++) perm[i] = i;
        Array.Sort(perm, byKey);
        return perm;
    }

    private static List<T> ApplyProbePermutation<T>(IReadOnlyList<T> src, int[] perm)
    {
        var reordered = new List<T>(src.Count);
        for (int i = 0; i < perm.Length; i++) reordered.Add(src[perm[i]]);
        return reordered;
    }

    /// <summary>Triple-keyed presence probe for attestations, which are partitioned
    /// LIST(type_id) -> HASH(subject_id): passing type and subject lets the probe prune
    /// to one leaf per id, where an id-only probe would descend every leaf's index.</summary>
    private Task<HashSet<Hash128>> ProbePresentKeyedParallelAsync(
        string queryName, IReadOnlyList<Hash128> ids, IReadOnlyList<Hash128> typeIds,
        IReadOnlyList<Hash128> subjectIds, Action<int> addRoundTrips, CancellationToken ct)
    {
        if (typeIds.Count != ids.Count || subjectIds.Count != ids.Count)
            throw new InvalidOperationException(
                $"keyed probe arrays misaligned: {ids.Count} ids / {typeIds.Count} types / {subjectIds.Count} subjects");
        return ProbePresentCoreAsync(queryName, ids,
            (parameters, start, n) =>
            {
                var chunkTypes = new byte[n][];
                var chunkSubjects = new byte[n][];
                for (int i = 0; i < n; i++)
                {
                    chunkTypes[i] = typeIds[start + i].ToBytes();
                    chunkSubjects[i] = subjectIds[start + i].ToBytes();
                }
                parameters.Add(new NpgsqlParameter
                { Value = chunkTypes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                parameters.Add(new NpgsqlParameter
                { Value = chunkSubjects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            }, addRoundTrips, ct);
    }
}
