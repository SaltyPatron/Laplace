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

    // ── O(tier) dedup-by-construction, AT THE BUILDER (2026-06-06) ──────────
    // Identity rows (entities/physicalities) are content-addressed: the same id
    // re-added within an intent is the SAME row — append once, skip repeats.
    // Testimony (attestations) is NOT idempotent: every occurrence is a Glicko
    // GAME. Repeats of one relation id FOLD onto its single evidence row —
    // observation_count = Σ games, SumScoreFp1e9 = exact Σ score (int64,
    // checked; bit-identical to per-occurrence accumulation), net outcome
    // CLASS, max timestamp. Before this, every occurrence shipped its own row:
    // ~95% of the wire/staging traffic was discarded by ON CONFLICT and the
    // evidence observation_count stayed 1 forever (measured 33.5M rows,
    // games_per_row = 1.000 — the fold law unhonored).
    private readonly HashSet<Hash128> _seenEntities = new();
    private readonly HashSet<Hash128> _seenPhysicalities = new();
    private readonly Dictionary<Hash128, int> _attestationIndex = new();

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
        ArgumentNullException.ThrowIfNull(row);
        if (_seenEntities.Add(row.Id)) _entities.Add(row);
        return this;
    }

    public SubstrateChangeBuilder AddEntity(
        Hash128 id, byte tier, Hash128 typeId, Hash128? firstObservedBy = null)
        => AddEntity(new EntityRow(id, tier, typeId, firstObservedBy));

    public SubstrateChangeBuilder AddPhysicality(PhysicalityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_seenPhysicalities.Add(row.Id)) _physicalities.Add(row);
        return this;
    }

    public SubstrateChangeBuilder AddAttestation(AttestationRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_attestationIndex.TryGetValue(row.Id, out int at))
        {
            var prior = _attestations[at];
            // One relation × one source × one period ⇒ one φ. Mixed φ inside a
            // single intent is a decomposer bug — same invariant the consensus
            // accumulation enforces; fail loud, never average.
            if (prior.OpponentRdFp1e9 != row.OpponentRdFp1e9)
                throw new InvalidOperationException(
                    $"attestation fold invariant violated: relation observed with φ={row.OpponentRdFp1e9} after φ={prior.OpponentRdFp1e9} in one intent");

            long games = checked(prior.ObservationCount + row.ObservationCount);
            long sum   = checked(
                (prior.SumScoreFp1e9 ?? checked(prior.ScoreFp1e9 * prior.ObservationCount))
                + (row.SumScoreFp1e9 ?? checked(row.ScoreFp1e9 * row.ObservationCount)));
            // Net outcome CLASS over the folded games (evidence law: outcome is
            // a class, never a magnitude): Σscore vs the all-draws baseline.
            long draws = checked(games * 500_000_000L);
            var net = sum > draws ? AttestationOutcome.Confirm
                    : sum < draws ? AttestationOutcome.Refute
                                  : AttestationOutcome.Draw;
            _attestations[at] = prior with
            {
                Outcome              = net,
                LastObservedAtUnixUs = Math.Max(prior.LastObservedAtUnixUs, row.LastObservedAtUnixUs),
                ObservationCount     = games,
                SumScoreFp1e9        = sum,
            };
        }
        else
        {
            _attestationIndex[row.Id] = _attestations.Count;
            _attestations.Add(row);
        }
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
