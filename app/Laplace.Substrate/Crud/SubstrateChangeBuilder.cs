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
    private readonly Dictionary<Hash128, int> _physByEntity = new();
    private int _physIndexWatermark;

    // The canonical member order for a set composition. memcmp of the 16-byte host layout,
    // which is exactly hash128_compare — the same order the native side and the substrate's
    // id ranges use, so a set composed here and one composed in C agree.
    private readonly struct Hash128Bytewise : IComparer<Hash128>
    {
        public int Compare(Hash128 x, Hash128 y) => x.CompareToBytewise(y);
    }
    private readonly Dictionary<Hash128, int> _attestationIndex = new();
    private readonly List<IntentStage> _intentStages = new();
    private readonly List<TestimonyWalkRow> _walks = new();

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
        _entities = ImmutableArray.CreateBuilder<EntityRow>(entityCapacity);
        _physicalities = ImmutableArray.CreateBuilder<PhysicalityRow>(physicalityCapacity);
        _attestations = ImmutableArray.CreateBuilder<AttestationRow>(attestationCapacity);
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

    // Dedup BEFORE constructing the row. The id is the whole dedup key, so the EntityRow only
    // needs to exist on a miss — and misses are the minority by an order of magnitude.
    // MEASURED (IngestThroughputProbe, 400 games / 28,149 plies): the chess lane issues ~2,380
    // AddNode calls per game and keeps 227 entities / 222 physicalities. ~90% of the row objects
    // were allocated and immediately dropped by the HashSet test below, and "row building" was
    // 49.9% of full record+analyze — the single largest cost in the compose path.
    public SubstrateChangeBuilder AddEntity(
        Hash128 id, byte tier, Hash128 typeId, Hash128? firstObservedBy = null)
    {
        if (_seenEntities.Add(id)) _entities.Add(new EntityRow(id, tier, typeId, firstObservedBy));
        return this;
    }

    public SubstrateChangeBuilder AddPhysicality(PhysicalityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_seenPhysicalities.Add(row.Id)) _physicalities.Add(row);
        return this;
    }

    /// <summary>
    /// Stage a physicality whose id the caller has ALREADY claimed through
    /// <see cref="TrySeePhysicality"/>. Lets a hot path skip constructing the row at all when the
    /// id is a repeat, instead of building it and discarding it here. Calling this without having
    /// claimed the id first bypasses dedup and can stage a duplicate — pair the two, always:
    /// <c>if (b.TrySeePhysicality(id)) b.AddPhysicalityPreSeen(new PhysicalityRow(id, ...));</c>
    /// </summary>
    public SubstrateChangeBuilder AddPhysicalityPreSeen(PhysicalityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        // The precondition is unenforceable at zero cost in release — the whole point of this
        // overload is to skip the hash lookup AddPhysicality would do. But a mistaken call site
        // stages a duplicate SILENTLY, and a duplicate physicality is exactly the class of bug
        // that surfaces later as a COPY dying on 23505 with no pointer to who staged it
        // (NpgsqlWorkingSetApply:468 records that failure mode costing a whole batch retry).
        // So assert it in DEBUG, where tests run: _seenPhysicalities is authoritative and the
        // check is exact.
        System.Diagnostics.Debug.Assert(
            _seenPhysicalities.Contains(row.Id),
            "AddPhysicalityPreSeen called without a prior TrySeePhysicality(row.Id) claim — "
            + "this bypasses dedup and stages a duplicate. Pair them: "
            + "if (b.TrySeePhysicality(id)) b.AddPhysicalityPreSeen(new PhysicalityRow(id, ...));");
        _physicalities.Add(row);
        return this;
    }

    /// <summary>
    /// Stage the composition entity for an unordered SET of member ids and return the id a
    /// set-valued attribute should point at, so the attribute costs ONE attestation rather than
    /// one per member.
    /// </summary>
    /// <remarks>
    /// Members are sorted ascending by id and deduplicated before composition, so the returned id
    /// is a function of the SET and not of the order it arrived in. That is the whole
    /// deduplication mechanism: 186,562,442 HAS_FEATURE edges carry 145,619 distinct member sets
    /// (docs/specs/38 §0), and re-staging one of them re-derives the same id and adds no rows.
    ///
    /// Composition runs through <see cref="HashComposer.ComposeNode"/> — the compiled kernel that
    /// every compose path shares — and not through a second C# expression of the same arithmetic
    /// (INVENTION §15). Three consequences come from the kernel rather than from this method:
    /// a ONE-MEMBER set collapses to the member's own id and stages nothing (the tier-floor
    /// collapse law, <c>hash128.c:22</c>), so a degenerate "set" of one tag remains a direct edge
    /// to that tag; the merkle domain byte is the kernel's; and the coordinate is the centroid of
    /// the members' LIVE coordinates, hilbert-encoded.
    ///
    /// Member coordinates are read from physicalities already staged in this builder. A member
    /// with no staged physicality throws rather than defaulting: composing a point from absent
    /// constituents is the "content ids with no constituents behind them" defect INVENTION §15
    /// names, and it would mint a coordinate that no witness supports.
    ///
    /// <paramref name="tier"/> is recorded on the entity row as the collection's floor. It is not
    /// an input to the id — <c>hash128_merkle</c> discards its tier argument by law.
    /// </remarks>
    public Hash128 StageCollection(
        ReadOnlySpan<Hash128> members, byte tier, Hash128 typeId, Hash128 sourceId,
        long observedAtUnixUs = 0)
    {
        if (members.Length == 0)
            throw new ArgumentException("a collection needs at least one member", nameof(members));

        Span<Hash128> sorted = members.Length <= 32
            ? stackalloc Hash128[members.Length] : new Hash128[members.Length];
        members.CopyTo(sorted);
        sorted.Sort(default(Hash128Bytewise));

        int n = 1;
        for (int i = 1; i < sorted.Length; i++)
            if (sorted[i] != sorted[n - 1]) sorted[n++] = sorted[i];
        sorted = sorted[..n];

        IndexStagedPhysicalities();
        Span<double> coords = n <= 32 ? stackalloc double[n * 4] : new double[n * 4];
        for (int i = 0; i < n; i++)
        {
            if (!_physByEntity.TryGetValue(sorted[i], out int at))
                throw new InvalidOperationException(
                    $"StageCollection member {sorted[i]} has no physicality staged in this builder; "
                    + "stage the members first, or use the overload that supplies their "
                    + "coordinates — a centroid over absent constituents is not a witnessed "
                    + "coordinate (INVENTION §15)");
            var row = _physicalities[at];
            coords[i * 4 + 0] = row.CoordX;
            coords[i * 4 + 1] = row.CoordY;
            coords[i * 4 + 2] = row.CoordZ;
            coords[i * 4 + 3] = row.CoordM;
        }
        return StageCollectionSorted(sorted, coords, tier, typeId, sourceId, observedAtUnixUs);
    }

    /// <summary>
    /// <see cref="StageCollection(ReadOnlySpan{Hash128}, byte, Hash128, Hash128, long)"/> with the
    /// members' live coordinates supplied by the caller — for lanes whose members were emitted in
    /// an earlier batch and are therefore not in this builder's staged rows.
    /// </summary>
    /// <remarks>
    /// memberCoordsXyzm is four doubles per member, in the SAME order as <paramref name="members"/>;
    /// the pairing is re-established after the canonical sort, so the caller does not pre-sort.
    /// </remarks>
    public Hash128 StageCollection(
        ReadOnlySpan<Hash128> members, ReadOnlySpan<double> memberCoordsXyzm, byte tier,
        Hash128 typeId, Hash128 sourceId, long observedAtUnixUs = 0)
    {
        if (members.Length == 0)
            throw new ArgumentException("a collection needs at least one member", nameof(members));
        if (memberCoordsXyzm.Length != members.Length * 4)
            throw new ArgumentException(
                "memberCoordsXyzm must hold exactly four doubles per member",
                nameof(memberCoordsXyzm));

        // Sort an index permutation so each member keeps its own coordinate through the
        // canonical reorder. Sorting ids alone and then reading coords positionally is the
        // silent-corruption shape this exists to avoid.
        Span<int> order = members.Length <= 32
            ? stackalloc int[members.Length] : new int[members.Length];
        for (int i = 0; i < members.Length; i++) order[i] = i;
        Span<Hash128> keys = members.Length <= 32
            ? stackalloc Hash128[members.Length] : new Hash128[members.Length];
        members.CopyTo(keys);
        keys.Sort(order, default(Hash128Bytewise));

        Span<Hash128> sorted = members.Length <= 32
            ? stackalloc Hash128[members.Length] : new Hash128[members.Length];
        Span<double> coords = members.Length <= 32
            ? stackalloc double[members.Length * 4] : new double[members.Length * 4];
        int n = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            if (n > 0 && keys[i] == sorted[n - 1]) continue;
            sorted[n] = keys[i];
            int src = order[i];
            memberCoordsXyzm.Slice(src * 4, 4).CopyTo(coords.Slice(n * 4, 4));
            n++;
        }
        return StageCollectionSorted(
            sorted[..n], coords[..(n * 4)], tier, typeId, sourceId, observedAtUnixUs);
    }

    private Hash128 StageCollectionSorted(
        ReadOnlySpan<Hash128> sorted, ReadOnlySpan<double> coords, byte tier,
        Hash128 typeId, Hash128 sourceId, long observedAtUnixUs)
    {
        int n = sorted.Length;
        Span<double> centroid = stackalloc double[4];
        (Hash128 id, Hilbert128 hb) = HashComposer.ComposeNode(tier, sorted, coords, centroid);

        // n == 1 -> the kernel returned the member's own id. Staging a Set physicality for it
        // would claim the member IS a collection; the tier-floor law says it is just itself.
        if (n == 1) return id;

        AddEntity(id, tier, typeId, sourceId);

        Hash128 physId = PhysicalityId.Compute(id, PhysicalityType.Set);
        if (TrySeePhysicality(physId))
            AddPhysicalityPreSeen(new PhysicalityRow(
                Id: physId, EntityId: id, SourceId: sourceId,
                Type: PhysicalityType.Set,
                CoordX: centroid[0], CoordY: centroid[1], CoordZ: centroid[2], CoordM: centroid[3],
                HilbertIndex: hb,
                // Constituent IDENTITY, mantissa-packed — never coordinates (INVENTION §9, the
                // trajectory law). Positions move as witnesses re-adjudicate; identity does not.
                TrajectoryXyzm: Trajectory.Build(sorted), NConstituents: n,
                AlignmentResidual: null, SourceDim: null, ObservedAtUnixUs: observedAtUnixUs));
        return id;
    }

    // Entity -> index into _physicalities, extended from a watermark rather than maintained on
    // every AddPhysicality. The compose path stages ~2,380 physicalities per chess game to keep
    // ~222 and its measured cost is row construction, so it pays nothing for this until a caller
    // actually composes a set; the scan is then amortised O(1) per staged row.
    private void IndexStagedPhysicalities()
    {
        for (; _physIndexWatermark < _physicalities.Count; _physIndexWatermark++)
            _physByEntity[_physicalities[_physIndexWatermark].EntityId] = _physIndexWatermark;
    }

    public bool TrySeeEntity(Hash128 id) => _seenEntities.Add(id);

    public bool TrySeePhysicality(Hash128 id) => _seenPhysicalities.Add(id);

    public SubstrateChangeBuilder AddIntentStage(IntentStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        _intentStages.Add(stage);
        return this;
    }

    private IntentStage? _contentStage;
    private ContentBatch? _deferredContent;

    public ContentBatch? DeferredContent => _deferredContent;

    /// <summary>
    /// Read-only presence oracle for the batch being composed: "has this id already been proven
    /// present by a COMMITTED apply?" Populated from the pipeline's containment reader.
    ///
    /// This is what lets a composer skip STAGING a subtree it knows is already deposited — the
    /// trunk short-circuit the shared content path has had all along (ContentTierSpine +
    /// ContentLadderLedger) and that the chess lane never joined. It answers false for anything
    /// not yet probed, so it can only ever cause work, never skip something absent: staging an
    /// already-present row is deduped at apply anyway, so a false negative costs exactly what
    /// today costs and a false positive is impossible.
    /// </summary>
    public ISubstrateReader? PresenceOracle { get; private set; }

    public SubstrateChangeBuilder SetPresenceOracle(ISubstrateReader? reader)
    {
        PresenceOracle = reader;
        return this;
    }

    public SubstrateChangeBuilder EnableDeferredContent(ISubstrateReader? reader)
    {
        if (reader is not null)
            _deferredContent ??= new ContentBatch(() => ContentStage, reader);
        return this;
    }

    public async Task<SubstrateChange> BuildAsync(CancellationToken ct = default)
    {
        if (_deferredContent is { HasPending: true } cb)
            await cb.ProbeAndFlushAsync(ct);
        return Build();
    }

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

    /// <summary>
    /// Staged bytes held by this builder — native COPY-tuple buffers plus a
    /// coarse per-row estimate for managed rows. Drives the working-set
    /// memory budget valve; an estimate, not an accounting.
    /// </summary>
    public long StagedBytesEstimate
    {
        get
        {
            long total = 0;
            foreach (var s in _intentStages)
                if (!s.IsInvalid) total += s.TotalTupleBytes;
            total += (long)_entities.Count * 72
                   + (long)_physicalities.Count * 160
                   + (long)_attestations.Count * 152;
            foreach (var p in _physicalities)
                if (p.TrajectoryXyzm is { } t) total += (long)t.Length * 8;
            return total;
        }
    }






    public SubstrateChangeBuilder AddTestimonyWalk(TestimonyWalkRow walk)
    {
        ArgumentNullException.ThrowIfNull(walk);
        _walks.Add(walk);
        return this;
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

            long games = AttestationMergeMath.SafeAddGames(prior.ObservationCount, row.ObservationCount);
            long sum = AttestationMergeMath.SafeAddScores(
                AttestationMergeMath.RowScoreTotal(prior),
                AttestationMergeMath.RowScoreTotal(row));
            var net = AttestationMergeMath.ClassifyOutcome(games, sum);
            _attestations[at] = prior with
            {
                Outcome = net,
                LastObservedAtUnixUs = Math.Max(prior.LastObservedAtUnixUs, row.LastObservedAtUnixUs),
                ObservationCount = games,
                SumScoreFp1e9 = sum,
            };
        }
        else
        {
            _attestationIndex[row.Id] = _attestations.Count;

            var withMask = row.HighwayMask.IsZero && HighwayPerfcache.IsLoaded
                ? row with { HighwayMask = HighwayPerfcache.MaskForRelationType(row.TypeId) }
                : row;
            _attestations.Add(withMask);
        }
        return this;
    }

    public SubstrateChange Build()
    {
        var entities = _entities.ToImmutable();
        var physicalities = _physicalities.ToImmutable();
        var attestations = _attestations.ToImmutable();

        var intentId = ComputeIntentId(_sourceId, _sourceContentUnitName,
                                        entities, physicalities, attestations);






        var stages = _intentStages.ToImmutableArray();
        _intentStages.Clear();
        _contentStage = null;

        var walks = _walks.ToImmutableArray();
        _walks.Clear();

        return new SubstrateChange(
            entities, physicalities, attestations,
            new SubstrateChangeMetadata(
                intentId,
                _sourceId,
                _sourceContentUnitName,
                IngestClock.Now(),
                _parentIntentId,
                _inputUnitsConsumed,
                _commitEpoch),
            stages,
            walks);
    }

    private static Hash128 ComputeIntentId(
        Hash128 sourceId,
        string unitName,
        ImmutableArray<EntityRow> entities,
        ImmutableArray<PhysicalityRow> physicalities,
        ImmutableArray<AttestationRow> attestations)
    {
        int nameByteCount = System.Text.Encoding.UTF8.GetByteCount(unitName);
        long total = 16L + nameByteCount
                     + 4L + (long)entities.Length * 16
                     + 4L + (long)physicalities.Length * 16
                     + 4L + (long)attestations.Length * 16;
        if (total > int.MaxValue)
        {
            throw new OverflowException(
                $"intent '{unitName}' too large to hash: {entities.Length} entities, "
                + $"{physicalities.Length} physicalities, {attestations.Length} attestations");
        }

        var buf = new byte[(int)total];
        int offset = 0;
        sourceId.WriteBytes(buf.AsSpan(offset, 16)); offset += 16;
        System.Text.Encoding.UTF8.GetBytes(unitName, 0, unitName.Length, buf, offset);
        offset += nameByteCount;
        WriteLengthAndIds(buf.AsSpan(), ref offset, entities, e => e.Id);
        WriteLengthAndIds(buf.AsSpan(), ref offset, physicalities, p => p.Id);
        WriteLengthAndIds(buf.AsSpan(), ref offset, attestations, a => a.Id);
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
