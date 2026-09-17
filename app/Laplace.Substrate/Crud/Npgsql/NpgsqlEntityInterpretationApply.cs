using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed partial class NpgsqlSubstrateWriter
{
    private readonly record struct EntityInterpretationKey(
        Hash128 EntityId, short Tier, Hash128 TypeId);

    /// <summary>
    /// Publish every structural interpretation observed by managed builders and
    /// native stages inside the apply control transaction. Canonical entity COPY
    /// remains id-only; this is the plural tier/type/provenance surface required
    /// by the identity law.
    /// </summary>
    private async Task<int> PersistEntityInterpretationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<IntentStage> stages,
        IReadOnlyList<EntityInterpretationRow> managed,
        CancellationToken ct)
    {
        var byKey = new Dictionary<EntityInterpretationKey, EntityInterpretationRow>();

        void Observe(EntityInterpretationRow row)
        {
            var key = new EntityInterpretationKey(row.EntityId, row.Tier, row.TypeId);
            if (!byKey.TryGetValue(key, out var prior))
            {
                byKey.Add(key, row);
                return;
            }

            // first_observed_by is compatibility provenance metadata. Preserve a
            // deterministic representative so execution/batch order cannot alter
            // the persisted state; exact testimony provenance lives in evidence.
            Hash128? source = MinNullableSource(prior.FirstObservedBy, row.FirstObservedBy);
            if (source != prior.FirstObservedBy)
                byKey[key] = prior with { FirstObservedBy = source };
        }

        foreach (var row in managed)
        {
            ct.ThrowIfCancellationRequested();
            Observe(row);
        }

        // Native/prebuilt/generated stages bypass SubstrateChangeBuilder, so decode
        // their complete entity rows before the apply core collapses canonical ids.
        var blobs = CollectBlobs(stages, IntentStageTable.Entities, 4, "entities");
        foreach (var row in CopyTupleParser.DecodeEntityRows(blobs))
        {
            ct.ThrowIfCancellationRequested();
            Observe(new EntityInterpretationRow(
                row.Id, row.Tier, row.TypeId, row.FirstObservedBy));
        }

        if (byKey.Count == 0) return 0;

        var rows = byKey.Values.ToArray();
        Array.Sort(rows, static (left, right) =>
        {
            int c = left.EntityId.CompareToBytewise(right.EntityId);
            if (c != 0) return c;
            c = left.Tier.CompareTo(right.Tier);
            if (c != 0) return c;
            c = left.TypeId.CompareToBytewise(right.TypeId);
            if (c != 0) return c;
            if (left.FirstObservedBy is null) return right.FirstObservedBy is null ? 0 : -1;
            if (right.FirstObservedBy is null) return 1;
            return left.FirstObservedBy.Value.CompareToBytewise(right.FirstObservedBy.Value);
        });

        // Five parallel arrays carry 16+2+16+16+1 payload bytes per row before
        // protocol/container overhead. Reuse the machine-derived flush envelope;
        // this changes transport grain only, never the admitted interpretation set.
        long envelope = IngestSizing.ResolveWorkingSetFlushEnvelopeBytes();
        int chunkRows = (int)Math.Max(1, Math.Min(Array.MaxLength,
            envelope / 96L));
        int roundTrips = 0;

        for (int start = 0; start < rows.Length; start += chunkRows)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(chunkRows, rows.Length - start);
            var ids = new byte[n][];
            var tiers = new short[n];
            var types = new byte[n][];
            var sources = new byte[n][];
            var sourceNull = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var row = rows[start + i];
                ids[i] = row.EntityId.ToBytes();
                tiers[i] = row.Tier;
                types[i] = row.TypeId.ToBytes();
                sourceNull[i] = row.FirstObservedBy is null;
                sources[i] = (row.FirstObservedBy ?? default).ToBytes();
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 0;
            command.CommandText = """
                WITH input AS MATERIALIZED (
                    SELECT u.entity_id, u.tier, u.type_id,
                           CASE WHEN u.source_is_null THEN NULL::bytea ELSE u.source_id END AS first_observed_by
                    FROM unnest($1::bytea[], $2::smallint[], $3::bytea[], $4::bytea[], $5::boolean[])
                         AS u(entity_id, tier, type_id, source_id, source_is_null)
                ), persisted AS (
                    INSERT INTO laplace.entity_interpretations AS ei
                        (entity_id, tier, type_id, first_observed_by)
                    SELECT entity_id, tier, type_id, first_observed_by
                    FROM input
                    ON CONFLICT (entity_id, tier, type_id) DO UPDATE
                    SET first_observed_by = CASE
                        WHEN ei.first_observed_by IS NULL THEN EXCLUDED.first_observed_by
                        WHEN EXCLUDED.first_observed_by IS NULL THEN ei.first_observed_by
                        WHEN ei.first_observed_by <= EXCLUDED.first_observed_by THEN ei.first_observed_by
                        ELSE EXCLUDED.first_observed_by
                    END
                    RETURNING entity_id
                ), summary AS MATERIALIZED (
                    SELECT i.entity_id,
                           min(i.tier)::smallint AS tier,
                           (array_agg(i.type_id ORDER BY i.type_id))[1] AS type_id,
                           (array_agg(i.first_observed_by ORDER BY i.first_observed_by)
                               FILTER (WHERE i.first_observed_by IS NOT NULL))[1] AS first_observed_by
                    FROM input i
                    GROUP BY i.entity_id
                ), updated AS (
                    UPDATE laplace.entities e
                       SET tier = LEAST(e.tier, s.tier),
                           type_id = LEAST(e.type_id, s.type_id),
                           first_observed_by = CASE
                               WHEN e.first_observed_by IS NULL THEN s.first_observed_by
                               WHEN s.first_observed_by IS NULL THEN e.first_observed_by
                               WHEN e.first_observed_by <= s.first_observed_by THEN e.first_observed_by
                               ELSE s.first_observed_by
                           END
                      FROM summary s
                     WHERE e.id = s.entity_id
                       AND (e.tier, e.type_id, e.first_observed_by) IS DISTINCT FROM
                           (LEAST(e.tier, s.tier), LEAST(e.type_id, s.type_id),
                            CASE
                                WHEN e.first_observed_by IS NULL THEN s.first_observed_by
                                WHEN s.first_observed_by IS NULL THEN e.first_observed_by
                                WHEN e.first_observed_by <= s.first_observed_by THEN e.first_observed_by
                                ELSE s.first_observed_by
                            END)
                    RETURNING e.id
                )
                SELECT (SELECT count(*) FROM persisted)::bigint,
                       (SELECT count(*) FROM updated)::bigint
                """;
            command.Parameters.Add(new NpgsqlParameter
            { Value = ids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            command.Parameters.Add(new NpgsqlParameter
            { Value = tiers, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint });
            command.Parameters.Add(new NpgsqlParameter
            { Value = types, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            command.Parameters.Add(new NpgsqlParameter
            { Value = sources, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            command.Parameters.Add(new NpgsqlParameter
            { Value = sourceNull, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Boolean });
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            roundTrips++;
        }

        return roundTrips;
    }

    private static Hash128? MinNullableSource(Hash128? left, Hash128? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left.Value.CompareToBytewise(right.Value) <= 0 ? left : right;
    }
}
