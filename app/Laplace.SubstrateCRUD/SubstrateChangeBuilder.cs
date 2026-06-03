using System.Collections.Immutable;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>
/// Fluent helper for constructing a <see cref="SubstrateChange"/> from a
/// populated <see cref="TierTree"/> + per-source attestations. Centralises
/// the slot-filling that every per-source decomposer would otherwise
/// reinvent (forbidden by).
///
/// FK-ordering invariant: entities → physicalities → attestations. The
/// builder appends to ordered lists; the apply path validates that
/// every physicality.entity_id refers to an entity in the same intent
/// (or to one already in substrate) and likewise for attestation FKs.
/// </summary>
public sealed class SubstrateChangeBuilder
{
    private readonly ImmutableArray<EntityRow>.Builder _entities;
    private readonly ImmutableArray<PhysicalityRow>.Builder _physicalities;
    private readonly ImmutableArray<AttestationRow>.Builder _attestations;
    private readonly Hash128 _sourceId;
    private readonly string _sourceContentUnitName;
    private readonly Hash128? _parentIntentId;

    public SubstrateChangeBuilder(
        Hash128 sourceId,
        string sourceContentUnitName,
        Hash128? parentIntentId = null,
        int entityCapacity = 16,
        int physicalityCapacity = 16,
        int attestationCapacity = 16)
    {
        _sourceId = sourceId;
        _sourceContentUnitName = sourceContentUnitName
            ?? throw new ArgumentNullException(nameof(sourceContentUnitName));
        _parentIntentId = parentIntentId;
        _entities      = ImmutableArray.CreateBuilder<EntityRow>(entityCapacity);
        _physicalities = ImmutableArray.CreateBuilder<PhysicalityRow>(physicalityCapacity);
        _attestations  = ImmutableArray.CreateBuilder<AttestationRow>(attestationCapacity);
    }

    public SubstrateChangeBuilder AddEntity(EntityRow row)
    {
        _entities.Add(row ?? throw new ArgumentNullException(nameof(row)));
        return this;
    }

    public SubstrateChangeBuilder AddEntity(
        Hash128 id, byte tier, Hash128 typeId, Hash128? firstObservedBy = null)
        => AddEntity(new EntityRow(id, tier, typeId, firstObservedBy));

    public SubstrateChangeBuilder AddPhysicality(PhysicalityRow row)
    {
        _physicalities.Add(row ?? throw new ArgumentNullException(nameof(row)));
        return this;
    }

    public SubstrateChangeBuilder AddAttestation(AttestationRow row)
    {
        _attestations.Add(row ?? throw new ArgumentNullException(nameof(row)));
        return this;
    }

    /// <summary>Build the immutable intent. <see cref="SubstrateChangeMetadata.IntentId"/>
    /// is derived deterministically from the row IDs so re-running the
    /// same decomposer on the same input produces the same intent ID —
    /// idempotent re-ingest (content-addressing + ON CONFLICT) relies on this.</summary>
    public SubstrateChange Build()
    {
        var entities      = _entities.ToImmutable();
        var physicalities = _physicalities.ToImmutable();
        var attestations  = _attestations.ToImmutable();

        // Intent ID = BLAKE3 over (sourceId ‖ source_content_unit_name_bytes ‖ row_id_bytes ...)
        // Stable across re-runs of the same decomposer on the same input.
        var intentId = ComputeIntentId(_sourceId, _sourceContentUnitName,
                                        entities, physicalities, attestations);

        return new SubstrateChange(
            entities, physicalities, attestations,
            new SubstrateChangeMetadata(
                intentId,
                _sourceId,
                _sourceContentUnitName,
                DateTimeOffset.UtcNow,
                _parentIntentId));
    }

    private static Hash128 ComputeIntentId(
        Hash128 sourceId,
        string unitName,
        ImmutableArray<EntityRow> entities,
        ImmutableArray<PhysicalityRow> physicalities,
        ImmutableArray<AttestationRow> attestations)
    {
        // Build the buffer: 16 bytes sourceId + utf8 bytes(unit_name)
        //   + (4 bytes entity_count + n*16 bytes entity ids)
        //   + (4 bytes physicality_count + n*16 bytes physicality ids)
        //   + (4 bytes attestation_count + n*16 bytes attestation ids)
        int nameByteCount = System.Text.Encoding.UTF8.GetByteCount(unitName);
        int total = 16 + nameByteCount
                    + 4 + entities.Length * 16
                    + 4 + physicalities.Length * 16
                    + 4 + attestations.Length * 16;
        var buf = new byte[total];
        int offset = 0;
        sourceId.WriteBytes(buf.AsSpan(offset, 16)); offset += 16;
        System.Text.Encoding.UTF8.GetBytes(unitName, 0, unitName.Length, buf, offset);
        offset += nameByteCount;
        WriteLengthAndIds(buf.AsSpan(), ref offset, entities,      e => e.Id);
        WriteLengthAndIds(buf.AsSpan(), ref offset, physicalities, p => p.Id);
        WriteLengthAndIds(buf.AsSpan(), ref offset, attestations,  a => a.Id);
        return Hash128.Blake3(buf);
    }

    private static void WriteLengthAndIds<T>(
        Span<byte> dst, ref int offset, ImmutableArray<T> rows, Func<T, Hash128> getId)
    {
        // Little-endian uint32 count
        int count = rows.Length;
        dst[offset++] = (byte)(count & 0xFF);
        dst[offset++] = (byte)((count >> 8) & 0xFF);
        dst[offset++] = (byte)((count >> 16) & 0xFF);
        dst[offset++] = (byte)((count >> 24) & 0xFF);
        for (int i = 0; i < count; i++)
        {
            getId(rows[i]).WriteBytes(dst.Slice(offset, 16));
            offset += 16;
        }
    }
}
