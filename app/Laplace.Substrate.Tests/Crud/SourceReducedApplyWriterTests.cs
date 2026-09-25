using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// The source-reduced order of operations stores the same evidence and standing as the
/// working-set writer for one source's claims, folds each claim exactly once, and folds a
/// source's claims onto the standing other witnesses already established.
/// </summary>
[Collection("substrate-pg")]
[Trait("Tier", "db")]
public class SourceReducedApplyWriterTests
{
    private readonly LocalPgFixture _pg;
    public SourceReducedApplyWriterTests(LocalPgFixture pg) => _pg = pg;

    private static Hash128 H(ulong n) => Hash128.OfCanonical($"substrate/test/source-reduced/{n}");

    // The claim identity is the five-tuple; its exact hash does not matter to these tests.
    private static Hash128 ClaimId(Hash128 subject, Hash128 type, Hash128 obj, Hash128 source, Hash128? context) =>
        Hash128.OfCanonical($"substrate/test/source-reduced/claim/{subject}/{type}/{obj}/{source}/{context}");

    private static AttestationRow Claim(Hash128 subject, Hash128 type, Hash128 obj, Hash128 source,
        long score, long games = 1, Hash128? context = null, Mask256 qualifiers = default)
    {
        Hash128 id = ClaimId(subject, type, obj, source, context);
        return new(id, subject, type, obj, source, context,
            Outcome: score > 500_000_000L ? AttestationOutcome.Confirm
                   : score < 500_000_000L ? AttestationOutcome.Refute : AttestationOutcome.Draw,
            LastObservedAtUnixUs: 1_770_000_000_000_000L, ObservationCount: games,
            ScoreFp1e9: score, OpponentRdFp1e9: 40_000_000_000L,
            OpponentRatingFp1e9: 1_700_000_000_000L, QualifierMask: qualifiers);
    }

    private static SubstrateChange Change(Hash128 source, string unit, params AttestationRow[] rows) =>
        new(ImmutableArray<EntityRow>.Empty, ImmutableArray<PhysicalityRow>.Empty, rows.ToImmutableArray(),
            new SubstrateChangeMetadata(Hash128.OfCanonical($"intent/source-reduced/{unit}"), source, unit,
                DateTimeOffset.UnixEpoch, null));

    private SourceReducedApplyWriter Reduced(ConsensusAccumulatingWriter inner) =>
        new(inner, _pg.DataSource, PostgresWriteDurability.Asynchronous, lanes: 3, budgetBytes: 1L << 30);

    private async Task<(long Rating, long Rd, long Volatility, long Witnesses)?> StandingAsync(
        Hash128 subject, Hash128 type, Hash128 obj)
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT rating, rd, volatility, witness_count FROM laplace.consensus "
            + "WHERE subject_id = $1 AND type_id = $2 AND object_id = $3");
        cmd.Parameters.AddWithValue(subject.ToBytes());
        cmd.Parameters.AddWithValue(type.ToBytes());
        cmd.Parameters.AddWithValue(obj.ToBytes());
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private async Task<(long Games, long Sum, byte[]? Mask)> EvidenceAsync(Hash128 id)
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT observation_count, sum_score_fp1e9, qualifier_mask FROM laplace.attestations WHERE id = $1");
        cmd.Parameters.AddWithValue(id.ToBytes());
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : (byte[])reader[2]);
    }

    [Fact]
    public async Task OneSourceStoresTheWorkingSetWritersStanding()
    {
        var source = H(1); var subject = H(2); var obj = H(3); var other = H(4);
        var viaWorkingSet = H(10); var viaReduction = H(11);
        AttestationRow[] Claims(Hash128 type) =>
        [
            Claim(subject, type, obj, source, 1_000_000_000, context: H(20)),
            Claim(subject, type, obj, source, 250_000_000, games: 3, context: H(21)),
            Claim(subject, type, other, source, 0),
        ];

        await using (var working = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource))
            await working.ApplyWorkingSetAsync(Change(source, "working", Claims(viaWorkingSet)));

        await using (var inner = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource))
        await using (var reduced = Reduced(inner))
        {
            await reduced.BeginBulkRunAsync();
            await reduced.ApplyWorkingSetAsync(Change(source, "reduced", Claims(viaReduction)));
            Assert.Null(await StandingAsync(subject, viaReduction, obj));
            await reduced.CompleteBulkRunAsync();
            Assert.Equal(5, reduced.ObservationsAccumulated - inner.ObservationsAccumulated);
        }

        Assert.Equal(await StandingAsync(subject, viaWorkingSet, obj), await StandingAsync(subject, viaReduction, obj));
        Assert.Equal(await StandingAsync(subject, viaWorkingSet, other), await StandingAsync(subject, viaReduction, other));
        Assert.Equal(4, (await StandingAsync(subject, viaReduction, obj))!.Value.Witnesses);
    }

    [Fact]
    public async Task AClaimFoldsExactlyOnce()
    {
        var source = H(30); var type = H(31); var subject = H(32); var obj = H(33);
        var claim = Claim(subject, type, obj, source, 1_000_000_000);
        await using var inner = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        await using var reduced = Reduced(inner);
        await reduced.ApplyWorkingSetAsync(Change(source, "once", claim));
        await reduced.CompleteBulkRunAsync();
        var standing = await StandingAsync(subject, type, obj);
        Assert.NotNull(standing);

        // The same claim again, and beside it a new one: only the new one folds.
        var fresh = Claim(subject, type, obj, source, 1_000_000_000, context: H(34));
        await reduced.ApplyWorkingSetAsync(Change(source, "again", claim, fresh));
        await reduced.CompleteBulkRunAsync();
        var after = await StandingAsync(subject, type, obj);
        Assert.Equal(2, after!.Value.Witnesses);

        await using var expectedInner = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        var type2 = H(35);
        await expectedInner.ApplyWorkingSetAsync(Change(source, "once-2", Claim(subject, type2, obj, source, 1_000_000_000)));
        await expectedInner.ApplyWorkingSetAsync(Change(source, "again-2",
            Claim(subject, type2, obj, source, 1_000_000_000, context: H(34))));
        Assert.Equal(await StandingAsync(subject, type2, obj), after);
    }

    [Fact]
    public async Task ASourceFoldsOntoAnotherWitnesssStanding()
    {
        var first = H(40); var second = H(41); var subject = H(42); var obj = H(43);
        var viaWorkingSet = H(44); var viaReduction = H(45);
        foreach (var type in new[] { viaWorkingSet, viaReduction })
        {
            await using var working = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
            await working.ApplyWorkingSetAsync(Change(first, $"first/{type}", Claim(subject, type, obj, first, 1_000_000_000, games: 2)));
        }
        await using (var working = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource))
            await working.ApplyWorkingSetAsync(Change(second, "second/working", Claim(subject, viaWorkingSet, obj, second, 0)));
        await using (var inner = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource))
        await using (var reduced = Reduced(inner))
        {
            await reduced.ApplyWorkingSetAsync(Change(second, "second/reduced", Claim(subject, viaReduction, obj, second, 0)));
            await reduced.CompleteBulkRunAsync();
        }
        var expected = await StandingAsync(subject, viaWorkingSet, obj);
        Assert.Equal(3, expected!.Value.Witnesses);
        Assert.Equal(expected, await StandingAsync(subject, viaReduction, obj));
    }

    [Fact]
    public async Task ARepeatedClaimIsOneClaimWithEveryQualifier()
    {
        var source = H(50); var type = H(51); var subject = H(52); var obj = H(53);
        var reference = new Mask256(1UL << 34, 0, 0, 0);
        var print = new Mask256(1UL << 35, 0, 0, 0);
        await using var inner = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        await using var reduced = Reduced(inner);
        await reduced.ApplyWorkingSetAsync(Change(source, "code-table",
            Claim(subject, type, obj, source, 1_000_000_000, qualifiers: reference)));
        await reduced.ApplyWorkingSetAsync(Change(source, "name-index",
            Claim(subject, type, obj, source, 1_000_000_000, qualifiers: print)));
        await reduced.CompleteBulkRunAsync();
        var (games, sum, mask) = await EvidenceAsync(ClaimId(subject, type, obj, source, null));
        Assert.Equal(2, games);
        Assert.Equal(2_000_000_000, sum);
        Assert.NotNull(mask);
        Assert.Equal(0x0c, mask![4]);
        Assert.Equal(2, (await StandingAsync(subject, type, obj))!.Value.Witnesses);
    }
}
