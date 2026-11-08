using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using global::Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed partial class ConsensusAccumulatingWriter
{
    /// <summary>
    /// Operational relation types that never fold into consensus: file-metadata edges
    /// ride inside ordinary working-set changes, so they are excluded row-by-row.
    /// Ingest completion is not here: it is operational state in its own tables and
    /// never reaches the attestation stream.
    /// </summary>
    private static readonly HashSet<Hash128> OpsMarkerTypeIds =
        [Laplace.Decomposers.Abstractions.FileEntity.MetadataRelationTypeId];

    /// <summary>Merges attestations [start, end) of the flattened block space into
    /// <paramref name="map"/>; returns the observation count it consumed.</summary>
    private static long MergeRange(
        List<ImmutableArray<AttestationRow>> blocks, long start, long end,
        Dictionary<(Hash128, Hash128, Hash128?), Delta> map,
        IReadOnlyDictionary<Hash128, EphemeralFoldInput> ephemeralByReceipt)
    {
        long obs = 0;
        long pos = 0;
        foreach (var atts in blocks)
        {
            long blockEnd = pos + atts.Length;
            if (blockEnd <= start) { pos = blockEnd; continue; }
            if (pos >= end) break;

            int from = (int)Math.Max(0, start - pos);
            int to = (int)Math.Min(atts.Length, end - pos);
            for (int i = from; i < to; i++)
            {
                var a = atts[i];
                if (OpsMarkerTypeIds.Contains(a.TypeId)) continue;
                bool hasEphemeral = ephemeralByReceipt.TryGetValue(a.Id, out var ephemeral);
                if (!a.FoldReplayable && !hasEphemeral)
                    throw new InvalidOperationException(
                        $"non-replayable receipt {a.Id} has no atomic ephemeral fold input");
                var key = (a.SubjectId, a.TypeId, a.ObjectId);
                long score = hasEphemeral
                    ? checked(ephemeral!.ScoreFp1e9 * a.ObservationCount)
                    : AttestationMergeMath.RowScoreTotal(a);
                ref var d = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out bool existed);
                if (!existed)
                {
                    InitObservationPeriods(
                        ref d, a.OpponentRatingFp1e9, a.OpponentRdFp1e9,
                        a.ObservationCount, score, a.LastObservedAtUnixUs);
                }
                else
                {
                    FoldInto(ref d, a.OpponentRdFp1e9, a.OpponentRatingFp1e9,
                             a.ObservationCount,
                             score, a.LastObservedAtUnixUs);
                }
                obs += a.ObservationCount;
            }
            pos = blockEnd;
        }
        return obs;
    }

    private static void InitObservationPeriods(
        ref Delta d, long oppRating, long phi, long games, long score, long tsUnixUs)
    {
        long unitScore = games > 0 ? score / games : 0;
        long rem = games > 0 ? score - (unitScore * games) : 0;
        if (rem == 0)
        {
            d.FirstPeriod = new(oppRating, phi, unitScore);
            d.FirstAggregate = new() { Games = games, SumScoreFp1e9 = score };
        }
        else
        {
            long step = rem > 0 ? 1L : -1L;
            long remCount = Math.Abs(rem);
            long baseCount = games - remCount;
            long remUnit = unitScore + step;
            d.FirstPeriod = new(oppRating, phi, remUnit);
            d.FirstAggregate = new()
            {
                Games = remCount,
                SumScoreFp1e9 = checked(remUnit * remCount),
            };
            if (baseCount > 0)
                AddPeriod(
                    ref d, new(oppRating, phi, unitScore),
                    baseCount, checked(unitScore * baseCount));
        }
        d.Games = games;
        d.SumScoreFp1e9 = score;
        d.MaxTsUnixUs = tsUnixUs;
    }

    private static void FoldInto(ref Delta d, long phi, long oppRating, long games, long score, long tsUnixUs)
    {
        long unitScore = games > 0 ? score / games : 0;
        long rem = games > 0 ? score - (unitScore * games) : 0;
        if (rem == 0)
        {
            AddPeriod(ref d, new(oppRating, phi, unitScore), games, score);
        }
        else
        {
            long step = rem > 0 ? 1L : -1L;
            long remCount = Math.Abs(rem);
            long baseCount = games - remCount;
            long remUnit = unitScore + step;
            AddPeriod(ref d, new(oppRating, phi, remUnit), remCount, checked(remUnit * remCount));
            if (baseCount > 0)
                AddPeriod(ref d, new(oppRating, phi, unitScore), baseCount, checked(unitScore * baseCount));
        }
        d.Games = AttestationMergeMath.SafeAddGames(d.Games, games);
        d.SumScoreFp1e9 = AttestationMergeMath.SafeAddScores(d.SumScoreFp1e9, score);
        if (tsUnixUs > d.MaxTsUnixUs) d.MaxTsUnixUs = tsUnixUs;
    }

    private static void FoldDelta(ref Delta d, in Delta src)
    {
        AddPeriod(ref d, src.FirstPeriod, src.FirstAggregate.Games,
                  src.FirstAggregate.SumScoreFp1e9);
        if (src.AdditionalPeriods is not null)
            foreach (var (key, value) in src.AdditionalPeriods)
                AddPeriod(ref d, key, value.Games, value.SumScoreFp1e9);
        d.Games = AttestationMergeMath.SafeAddGames(d.Games, src.Games);
        d.SumScoreFp1e9 = AttestationMergeMath.SafeAddScores(
            d.SumScoreFp1e9, src.SumScoreFp1e9);
        if (src.MaxTsUnixUs > d.MaxTsUnixUs) d.MaxTsUnixUs = src.MaxTsUnixUs;
    }

    private static void AddPeriod(
        ref Delta d, PeriodKey key, long games, long sumScoreFp1e9)
    {
        if (key == d.FirstPeriod)
        {
            d.FirstAggregate.Games = AttestationMergeMath.SafeAddGames(
                d.FirstAggregate.Games, games);
            d.FirstAggregate.SumScoreFp1e9 = AttestationMergeMath.SafeAddScores(
                d.FirstAggregate.SumScoreFp1e9, sumScoreFp1e9);
            return;
        }

        d.AdditionalPeriods ??= new();
        ref var aggregate = ref CollectionsMarshal.GetValueRefOrAddDefault(
            d.AdditionalPeriods, key, out bool existed);
        if (!existed)
            aggregate = new() { Games = games, SumScoreFp1e9 = sumScoreFp1e9 };
        else
        {
            aggregate.Games = AttestationMergeMath.SafeAddGames(aggregate.Games, games);
            aggregate.SumScoreFp1e9 = AttestationMergeMath.SafeAddScores(
                aggregate.SumScoreFp1e9, sumScoreFp1e9);
        }
    }

    private static int PeriodCount(in Delta d) => 1 + (d.AdditionalPeriods?.Count ?? 0);

    private static void WritePeriods(
        in Delta d, long[] opponentRatings, long[] phis, long[] games,
        long[] sums, ref int at)
    {
        if (d.AdditionalPeriods is null)
        {
            opponentRatings[at] = d.FirstPeriod.OpponentRatingFp1e9;
            phis[at] = d.FirstPeriod.PhiFp1e9;
            games[at] = d.FirstAggregate.Games;
            sums[at++] = d.FirstAggregate.SumScoreFp1e9;
            return;
        }

        // Stable physical payload independent of input order, dictionary order,
        // which parallel shard became the merge target, and batch worker count.
        var periods = new KeyValuePair<PeriodKey, PeriodAggregate>[
            d.AdditionalPeriods.Count + 1];
        periods[0] = new(d.FirstPeriod, d.FirstAggregate);
        int copied = 1;
        foreach (var period in d.AdditionalPeriods)
            periods[copied++] = period;
        Array.Sort(periods, static (x, y) => x.Key.CompareTo(y.Key));
        foreach (var (key, aggregate) in periods)
        {
            opponentRatings[at] = key.OpponentRatingFp1e9;
            phis[at] = key.PhiFp1e9;
            games[at] = aggregate.Games;
            sums[at++] = aggregate.SumScoreFp1e9;
        }
    }

    private IReadOnlyList<SubstrateChange> ForwardChanges(IReadOnlyList<SubstrateChange> changes)
    {
        if (_persistEvidence) return changes;

        bool anyToStrip = false;
        foreach (var c in changes)
            if (!c.Attestations.IsEmpty) { anyToStrip = true; break; }
        if (!anyToStrip) return changes;

        var stripped = new SubstrateChange[changes.Count];
        for (int i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            if (!c.Attestations.IsEmpty)
                c = c with { Attestations = ImmutableArray<AttestationRow>.Empty };
            stripped[i] = c;
        }
        return stripped;
    }

    private readonly record struct AtomicFoldStats(
        long Cells,
        long Masks,
        long ConsensusBackendTicks,
        long MaskBackendTicks,
        long FoldCalls,
        long MaskCalls,
        long MaskPairs);

    /// <summary>
    /// Folds a journaled working set on the evidence writer's transaction. The
    /// journal replay claim, attestation merge, consensus merge and mask deposit
    /// commit or roll back together. Cells are sorted (type, edge id, subject) and
    /// mask pairs (entity, type), the same lock order the lanes use.
    /// </summary>
    private async Task<(AtomicFoldStats Stats, List<(Hash128 Entity, Hash128 Type)> MaskPairs)> UpsertDeltaInTransactionAsync(
        Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta> delta,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        var cells = new ((Hash128 S, Hash128 T, Hash128? O) Key, Hash128 Cid, Delta D)[delta.Count];
        int cellAt = 0;
        foreach (var (key, value) in delta)
            cells[cellAt++] = (key, ConsensusKeys.EdgeId(key.S, key.T, key.O ?? default), value);
        Array.Sort(cells, static (x, y) =>
        {
            int c = x.Key.T.CompareToBytewise(y.Key.T);
            if (c != 0) return c;
            c = x.Cid.CompareToBytewise(y.Cid);
            return c != 0 ? c : x.Key.S.CompareToBytewise(y.Key.S);
        });

        long folded = 0;
        long foldCalls = 0;
        long foldStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int off = 0; off < cells.Length; off += AtomicFoldChunkCells)
        {
            int count = Math.Min(AtomicFoldChunkCells, cells.Length - off);
            var subjects = new byte[count][];
            var types = new byte[count][];
            var objects = new byte[count][];
            var phis = new long[count];
            var opponents = new long[count];
            var games = new long[count];
            var sums = new long[count];
            var timestamps = new DateTime[count];
            int periodCount = 0;
            for (int i = 0; i < count; i++)
                periodCount = checked(periodCount + PeriodCount(in cells[off + i].D));
            var periodOffsets = new long[count + 1];
            var periodOpponents = new long[periodCount];
            var periodPhis = new long[periodCount];
            var periodGames = new long[periodCount];
            var periodSums = new long[periodCount];
            int periodAt = 0;
            for (int i = 0; i < count; i++)
            {
                var cell = cells[off + i];
                subjects[i] = cell.Key.S.ToBytes();
                types[i] = cell.Key.T.ToBytes();
                objects[i] = cell.Key.O?.ToBytes()!;
                phis[i] = cell.D.FirstPeriod.PhiFp1e9;
                opponents[i] = cell.D.FirstPeriod.OpponentRatingFp1e9;
                games[i] = cell.D.Games;
                sums[i] = cell.D.SumScoreFp1e9;
                timestamps[i] = TsFromUnixUs(cell.D.MaxTsUnixUs);
                periodOffsets[i] = periodAt;
                WritePeriods(
                    in cell.D, periodOpponents, periodPhis,
                    periodGames, periodSums, ref periodAt);
            }
            periodOffsets[count] = periodAt;

            // One call folds the whole mixed-type chunk; native code groups it by
            // type partition, locks and folds each type run.
            await using var command = NpgsqlCatalog.Command(connection, transaction, "consensus.merge_evidence",
                subjects, types, objects, phis, games, sums, timestamps, opponents, periodOffsets,
                periodOpponents, periodPhis, periodGames, periodSums);
            command.CommandTimeout = 0;
            folded += (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
            foldCalls++;
        }
        long foldTicks = System.Diagnostics.Stopwatch.GetTimestamp() - foldStarted;

        var maskPairs = new HashSet<(Hash128 Entity, Hash128 Type)>(cells.Length * 2);
        foreach (var cell in cells)
        {
            AddUnseenMaskPair(maskPairs, (cell.Key.S, cell.Key.T));
            if (cell.Key.O is { } objectId) AddUnseenMaskPair(maskPairs, (objectId, cell.Key.T));
        }
        var orderedPairs = maskPairs.ToList();
        orderedPairs.Sort(static (x, y) =>
        {
            int c = x.Entity.CompareToBytewise(y.Entity);
            return c != 0 ? c : x.Type.CompareToBytewise(y.Type);
        });

        long masks = 0;
        long maskCalls = 0;
        long maskStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int off = 0; off < orderedPairs.Count; off += AtomicFoldChunkCells)
        {
            int count = Math.Min(AtomicFoldChunkCells, orderedPairs.Count - off);
            var entities = new byte[count][];
            var maskTypes = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                entities[i] = orderedPairs[off + i].Entity.ToBytes();
                maskTypes[i] = orderedPairs[off + i].Type.ToBytes();
            }
            await using var command = NpgsqlCatalog.Command(connection, transaction,
                "consensus.deposit_mask", entities, maskTypes);
            command.CommandTimeout = 0;
            masks += (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
            maskCalls++;
        }
        long maskTicks = System.Diagnostics.Stopwatch.GetTimestamp() - maskStarted;
        return (
            new AtomicFoldStats(
                folded, masks, foldTicks, maskTicks,
                foldCalls, maskCalls, orderedPairs.Count),
            orderedPairs);
    }

    private void AddUnseenMaskPair(
        HashSet<(Hash128 Entity, Hash128 Type)> target,
        (Hash128 Entity, Hash128 Type) pair)
    {
        var deposited = _depositedMaskPairs[MaskShard(pair.Entity)];
        lock (deposited)
        {
            if (deposited.Contains(pair)) return;
        }
        target.Add(pair);
    }

    private void RecordCommittedMaskPairs(List<(Hash128 Entity, Hash128 Type)>? pairs)
    {
        if (pairs is not { Count: > 0 }) return;
        int capPerShard = DepositedMaskPairsCap / MaskShards;
        foreach (var pair in pairs)
        {
            var deposited = _depositedMaskPairs[MaskShard(pair.Entity)];
            lock (deposited)
            {
                if (deposited.Count + 1 > capPerShard)
                    deposited.Clear();
                deposited.Add(pair);
            }
        }
    }

}
