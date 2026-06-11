using System.Collections.Immutable;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

public sealed class SubstrateChangeBuilder
{
    private readonly ImmutableArray<EntityRow>.Builder _entities;
    private readonly ImmutableArray<PhysicalityRow>.Builder _physicalities;
    private readonly ImmutableArray<AttestationRow>.Builder _attestations;
    private readonly Hash128 _sourceId;
    private readonly string _sourceContentUnitName;
    private readonly Hash128? _parentIntentId;
    private long _inputUnitsConsumed;
    private int _commitEpoch;

    private readonly HashSet<Hash128> _seenEntities = new();
    private readonly HashSet<Hash128> _seenPhysicalities = new();
    private readonly Dictionary<Hash128, int> _attestationIndex = new();
    private readonly List<IntentStage> _intentStages = new();

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

    public SubstrateChangeBuilder SetInputUnitsConsumed(long n)
    {
        _inputUnitsConsumed = n;
        return this;
    }

    public SubstrateChangeBuilder SetCommitEpoch(int epoch)
    {
        _commitEpoch = epoch;
        return this;
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

    public SubstrateChangeBuilder AddIntentStage(IntentStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        _intentStages.Add(stage);
        return this;
    }

    private IntentStage? _contentStage;

    /// <summary>
    /// THE coalesced content stage for this change: every content witness appends
    /// here, so the writer pays its staging round trips once per change instead of
    /// once per witness (the per-witness pattern cost iso639 16,681 round trips
    /// for 51k rows). Lazily created and attached on first use; the writer owns
    /// disposal after staging, same as any prebuilt stage.
    /// </summary>
    public IntentStage ContentStage
    {
        get
        {
            if (_contentStage is null || _contentStage.IsInvalid)
            {
                _contentStage = IntentStage.New(256);
                _intentStages.Add(_contentStage);
            }
            return _contentStage;
        }
    }

    public SubstrateChangeBuilder AddAttestation(AttestationRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_attestationIndex.TryGetValue(row.Id, out int at))
        {
            var prior = _attestations[at];
            if (prior.OpponentRdFp1e9 != row.OpponentRdFp1e9)
                throw new InvalidOperationException(
                    $"attestation fold invariant violated: relation observed with φ={row.OpponentRdFp1e9} after φ={prior.OpponentRdFp1e9} in one intent");

            long games = checked(prior.ObservationCount + row.ObservationCount);
            long sum   = checked(
                (prior.SumScoreFp1e9 ?? checked(prior.ScoreFp1e9 * prior.ObservationCount))
                + (row.SumScoreFp1e9 ?? checked(row.ScoreFp1e9 * row.ObservationCount)));
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

    public SubstrateChange Build()
    {
        var entities      = _entities.ToImmutable();
        var physicalities = _physicalities.ToImmutable();
        var attestations  = _attestations.ToImmutable();

        var intentId = ComputeIntentId(_sourceId, _sourceContentUnitName,
                                        entities, physicalities, attestations);

        return new SubstrateChange(
            entities, physicalities, attestations,
            new SubstrateChangeMetadata(
                intentId,
                _sourceId,
                _sourceContentUnitName,
                DateTimeOffset.UtcNow,
                _parentIntentId,
                _inputUnitsConsumed,
                _commitEpoch),
            _intentStages.ToImmutableArray());
    }

    private static Hash128 ComputeIntentId(
        Hash128 sourceId,
        string unitName,
        ImmutableArray<EntityRow> entities,
        ImmutableArray<PhysicalityRow> physicalities,
        ImmutableArray<AttestationRow> attestations)
    {
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
