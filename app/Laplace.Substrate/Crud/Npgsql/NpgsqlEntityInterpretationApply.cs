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
        IReadOnlyList<EntityInterpretationRow> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return 0;

        // Five parallel arrays carry 16+2+16+16+1 payload bytes per row before
        // protocol/container overhead. Reuse the machine-derived flush envelope;
        // this changes transport grain only, never the admitted interpretation set.
        long envelope = IngestSizing.ResolveWorkingSetFlushEnvelopeBytes();
        int chunkRows = (int)Math.Max(1, Math.Min(Array.MaxLength,
            envelope / 96L));
        int roundTrips = 0;

        for (int start = 0; start < rows.Count; start += chunkRows)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(chunkRows, rows.Count - start);
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
            command.CommandText = SqlCatalog.Get("ingest.entity_interpretations").Text;
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
            bool missingEntity = (bool)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            if (missingEntity)
                throw new InvalidOperationException("entity interpretation references an absent canonical entity");
            roundTrips++;
        }

        return roundTrips;
    }


    private static (IntPtr Ptr,long Len) SelectedEntityInterpretationBuffer(
        IntentStage stage,out int count)
    {
        bool complete = stage.EntityInterpretationsComplete;
        count = complete ? stage.EntityInterpretationCount : stage.EntityCount;
        if (count == 0) return default;
        var (pointer,length) = complete
            ? stage.EntityInterpretationTupleBuffer()
            : stage.TupleBuffer(IntentStageTable.Entities);
        if (pointer == IntPtr.Zero || length <= 0)
            throw new InvalidOperationException("native entity interpretation count has no tuple payload");
        if (CopyBlobValidator.Enabled)
            CopyBlobValidator.Validate(pointer,length,4,"entity_interpretations",count);
        return (pointer,length);
    }

    internal static EntityInterpretationRow[] CollectEntityInterpretations(
        IReadOnlyList<IntentStage> stages, IEnumerable<EntityInterpretationRow> managed,
        CancellationToken ct = default)
    {
        var observed = new List<EntityInterpretationRow>();
        observed.AddRange(managed);
        foreach (var stage in stages)
        {
            ct.ThrowIfCancellationRequested();
            var blob = SelectedEntityInterpretationBuffer(stage,out int count);
            var native = count == 0 ? new List<EntityRow>()
                : CopyTupleParser.DecodeEntityRows([blob]);
            if (stage.EntityInterpretationsComplete && stage.EntityCount > 0)
            {
                var covered = native.Select(static row => row.Id).ToHashSet();
                var raw = CopyTupleParser.ParseEntities(
                    CollectBlobs([stage],IntentStageTable.Entities,4,"entities"));
                if (raw.Ids.Any(id => !covered.Contains(id)))
                    throw new InvalidOperationException(
                        "complete native interpretation stream omits a staged canonical entity");
            }
            foreach (var row in native)
                observed.Add(new EntityInterpretationRow(
                    row.Id,row.Tier,row.TypeId,row.FirstObservedBy));
        }
        // Complete native metadata owns the actual observed pairs, even where
        // historical E bytes carry a compatibility projection such as lowered
        // AST altitude. The historical semantic digest still owns those bytes.
        var result = CanonicalEntityInterpretations(observed,ct);
        GC.KeepAlive(stages);
        return result;
    }

    private static CopyTupleParser.EntityRows CollectEntityCopyRows(
        IReadOnlyList<IntentStage> stages,out List<(IntPtr Ptr,long Len)> blobs)
    {
        var eligible = new HashSet<Hash128>();
        blobs = new List<(IntPtr Ptr,long Len)>(stages.Count);
        foreach (var stage in stages)
        {
            if (stage.EntityCount == 0) continue;
            var raw = CopyTupleParser.ParseEntities(
                CollectBlobs([stage],IntentStageTable.Entities,4,"entities"));
            eligible.UnionWith(raw.Ids);
            var blob = SelectedEntityInterpretationBuffer(stage,out int count);
            if (count == 0)
                throw new InvalidOperationException(
                    "complete native interpretation stream omits a staged canonical entity");
            blobs.Add(blob);
        }
        var facets = CopyTupleParser.ParseEntities(blobs);
        var rows = new CopyTupleParser.EntityRows();
        for (int i=0;i<facets.Ids.Count;i++)
        {
            if (!eligible.Contains(facets.Ids[i])) continue;
            rows.Ids.Add(facets.Ids[i]);
            rows.Tiers.Add(facets.Tiers[i]);
            rows.TypeIds.Add(facets.TypeIds[i]);
            rows.Rows.Add(facets.Rows[i]);
        }
        // Row references borrow the live original stages' selected facet buffers.
        // Auxiliary-only IDs are never made eligible for canonical entity COPY.
        return rows;
    }

    internal static EntityInterpretationRow[] CanonicalEntityInterpretations(
        IEnumerable<EntityInterpretationRow> input, CancellationToken ct = default)
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

        foreach (var row in input)
        {
            ct.ThrowIfCancellationRequested();
            Observe(row);
        }

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

        return rows;
    }

    internal static Hash128 InterpretationReplayToken(
        Hash128 semanticToken, IEnumerable<EntityInterpretationRow> interpretations)
    {
        var rows = CanonicalEntityInterpretations(interpretations);
        ReadOnlySpan<byte> domain = "LaplaceInterpretationAdmission/v1\0"u8;
        long length = domain.Length + 16L + 4L + rows.Length * 50L;
        if (length > Array.MaxLength)
            throw new OverflowException("interpretation admission digest exceeds its managed buffer limit");
        var payload = new byte[(int)length];
        domain.CopyTo(payload);
        int offset = domain.Length;
        semanticToken.WriteBytes(payload.AsSpan(offset, 16)); offset += 16;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(offset, 4), rows.Length); offset += 4;
        foreach (var row in rows)
        {
            row.EntityId.WriteBytes(payload.AsSpan(offset, 16)); offset += 16;
            payload[offset++] = row.Tier;
            row.TypeId.WriteBytes(payload.AsSpan(offset, 16)); offset += 16;
            payload[offset++] = row.FirstObservedBy.HasValue ? (byte)1 : (byte)0;
            (row.FirstObservedBy ?? default).WriteBytes(payload.AsSpan(offset, 16)); offset += 16;
        }
        return Hash128.Blake3(payload);
    }

    private static Hash128? MinNullableSource(Hash128? left, Hash128? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left.Value.CompareToBytewise(right.Value) <= 0 ? left : right;
    }
}
