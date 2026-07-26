using Laplace.Decomposers.Wiktionary;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// The ledger lets ContentTierSpine.TryStageIntoBuilder answer "this surface's ladder is
/// already deposited" BEFORE deriving the ladder — closing the re-emit path that put
/// hundreds of thousands of already-present rows into the working-set apply's merge lane
/// on every batch boundary.
///
/// A skip that fires wrongly does not make the ingest slow, it makes it LOSSY: the ladder
/// never stages and the entity never lands. So the invariants pinned here are the ones
/// that make a wrong skip impossible —
///   1. disarmed  => never skip (default state for every non-bulk caller and every test);
///   2. armed but unrecorded => never skip;
///   3. recorded => skip, and hand back the SAME root id the deriving path produces.
/// Identity is exact, so (3) is the one that would silently fork the substrate.
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class ContentLadderLedgerTests : IDisposable
{
    private static readonly Hash128 Source = WiktionaryDecomposer.Source;

    public ContentLadderLedgerTests() => ContentLadderLedger.End();

    // Static run-scoped state in a shared-process suite: leave it disarmed on the way out
    // so no later test inherits an armed ledger.
    public void Dispose() => ContentLadderLedger.End();

    private static SubstrateChangeBuilder NewBuilder(string context) =>
        new(Source, context, null,
            entityCapacity: 64, physicalityCapacity: 64, attestationCapacity: 64);

    private static Hash128 Stage(string surface, string context)
    {
        var b = NewBuilder(context);
        Assert.True(ContentTierSpine.TryStageIntoBuilder(
            b, System.Text.Encoding.UTF8.GetBytes(surface), Source, out var id));
        return id;
    }

    public static TheoryData<string> Surfaces() => new()
    {
        "filter",   // ordinary word
        "en",       // language code — the extreme repeat class
        "a",        // single codepoint
        "café",     // multi-byte UTF-8
        "汉字",      // non-Latin
        "𝄞",        // astral plane
        "New York", // space-bearing, multi-word
    };

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void Disarmed_never_skips_and_keeps_the_derived_id(string surface)
    {
        ContentLadderLedger.End();
        Assert.False(ContentLadderLedger.Armed);

        var first = Stage(surface, "disarmed-1");
        var again = Stage(surface, "disarmed-2");

        Assert.NotEqual(default, first);
        Assert.Equal(first, again);
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void Armed_but_unrecorded_still_derives(string surface)
    {
        var derived = Stage(surface, "baseline");

        ContentLadderLedger.Begin();
        Assert.True(ContentLadderLedger.Armed);
        Assert.False(ContentLadderLedger.IsPersisted(derived));

        Assert.Equal(derived, Stage(surface, "armed-empty"));
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void Recorded_skips_and_returns_the_identical_root(string surface)
    {
        var derived = Stage(surface, "baseline");

        ContentLadderLedger.Begin();
        ContentLadderLedger.MarkPersisted([derived]);
        Assert.True(ContentLadderLedger.IsPersisted(derived));

        // Bit-identical id from the skipping path — the whole point.
        Assert.Equal(derived, Stage(surface, "recorded"));

        // And the skip really skipped: nothing reaches the native ContentStage.
        var skipped = NewBuilder("recorded-empty");
        Assert.True(ContentTierSpine.TryStageIntoBuilder(
            skipped, System.Text.Encoding.UTF8.GetBytes(surface), Source, out var id));
        Assert.Equal(derived, id);
        Assert.Equal(0, skipped.ContentStage.EntityCount);
    }

    [Fact]
    public void The_skip_is_what_removes_the_staging_work()
    {
        // A multi-codepoint surface has a real ladder to stage — single codepoints do
        // not (tier 0 is a closed, already-seeded space, so the deriving path stages
        // nothing for them either and could not tell a skip from a no-op).
        const string surface = "New York";
        var control = NewBuilder("control");
        Assert.True(ContentTierSpine.TryStageIntoBuilder(
            control, System.Text.Encoding.UTF8.GetBytes(surface), Source, out var derived));
        int stagedWhenDeriving = control.ContentStage.EntityCount;
        Assert.True(stagedWhenDeriving > 0);

        ContentLadderLedger.Begin();
        ContentLadderLedger.MarkPersisted([derived]);

        var skipped = NewBuilder("skipped");
        Assert.True(ContentTierSpine.TryStageIntoBuilder(
            skipped, System.Text.Encoding.UTF8.GetBytes(surface), Source, out var id));

        Assert.Equal(derived, id);
        Assert.Equal(0, skipped.ContentStage.EntityCount);
    }

    [Fact]
    public void Recording_one_surface_does_not_skip_a_different_one()
    {
        var recorded = Stage("filter", "baseline");
        var other = Stage("filtered", "baseline");
        Assert.NotEqual(recorded, other);

        ContentLadderLedger.Begin();
        ContentLadderLedger.MarkPersisted([recorded]);

        Assert.True(ContentLadderLedger.IsPersisted(recorded));
        Assert.False(ContentLadderLedger.IsPersisted(other));
        Assert.Equal(other, Stage("filtered", "still-derives"));
    }

    [Fact]
    public void End_disarms_so_the_next_run_re_proves_presence()
    {
        var derived = Stage("filter", "baseline");
        ContentLadderLedger.Begin();
        ContentLadderLedger.MarkPersisted([derived]);
        Assert.True(ContentLadderLedger.IsPersisted(derived));

        ContentLadderLedger.End();

        Assert.False(ContentLadderLedger.Armed);
        Assert.False(ContentLadderLedger.IsPersisted(derived));
        Assert.Equal(derived, Stage("filter", "after-end"));
    }

    [Fact]
    public void Marking_outside_a_run_is_a_no_op()
    {
        var derived = Stage("filter", "baseline");
        ContentLadderLedger.End();

        ContentLadderLedger.MarkPersisted([derived]);

        Assert.False(ContentLadderLedger.IsPersisted(derived));
    }

    [Fact]
    public void Concurrent_marking_and_probing_agrees_with_the_derived_id()
    {
        var surfaces = new[] { "filter", "en", "café", "汉字", "New York", "a" };
        var derived = surfaces.Select(s => Stage(s, "baseline")).ToArray();

        ContentLadderLedger.Begin();
        ContentLadderLedger.MarkPersisted(derived);

        Parallel.For(0, 256, i =>
        {
            int k = i % surfaces.Length;
            Assert.Equal(derived[k], Stage(surfaces[k], $"concurrent-{i}"));
        });
    }
}
