using Laplace.Decomposers.Wiktionary;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Entity presence and exact root memoization must preserve identity while
/// retaining physicality observations for each source unit. A committed root
/// cannot act as a receipt for another observation.
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class ContentLadderLedgerTests : IDisposable
{
    private static readonly Hash128 Source = WiktionaryDecomposer.Source;

    public ContentLadderLedgerTests() => ContentLadderLedger.Reset();

    // Static process state: wipe membership on the way out so no later test inherits skips.
    public void Dispose() => ContentLadderLedger.Reset();

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
        ContentLadderLedger.Reset();
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

    [Fact]
    public void Armed_empty_run_memoizes_and_retains_observations_after_commit()
    {
        const string surface = "memoize before first committed apply";
        ContentLadderLedger.Begin();
        Assert.False(ContentLadderLedger.HasEntries);

        var first = NewBuilder("armed-empty-first");
        Assert.True(ContentTierSpine.TryStageIntoBuilder(
            first, System.Text.Encoding.UTF8.GetBytes(surface), Source, out var root));
        Assert.True(first.ContentStage.EntityCount > 0);

        ContentLadderLedger.MarkPersisted([root]);
        var repeated = NewBuilder("armed-after-commit");
        Assert.True(ContentTierSpine.TryStageIntoBuilder(
            repeated, System.Text.Encoding.UTF8.GetBytes(surface), Source, out var repeatedRoot));
        Assert.Equal(root, repeatedRoot);
        Assert.True(repeated.ContentStage.PhysicalityCount > 0);
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void Recorded_root_retains_observation_and_returns_the_identical_identity(string surface)
    {
        var derived = Stage(surface, "baseline");

        ContentLadderLedger.Begin();
        ContentLadderLedger.MarkPersisted([derived]);
        Assert.True(ContentLadderLedger.IsPersisted(derived));

        // Presence never changes the canonical root.
        Assert.Equal(derived, Stage(surface, "recorded"));

        // The new source unit still reaches the native physicality owner.
        var skipped = NewBuilder("recorded-empty");
        Assert.True(ContentTierSpine.TryStageIntoBuilder(
            skipped, System.Text.Encoding.UTF8.GetBytes(surface), Source, out var id));
        Assert.Equal(derived, id);
        Assert.True(skipped.ContentStage.PhysicalityCount > 0);
    }

    [Fact]
    public void Recorded_root_preserves_the_actual_source_span()
    {
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
        Assert.True(skipped.ContentStage.PhysicalityCount > 0);
        var range = Assert.Single(skipped.ContentStage.PhysicalitySourceRanges);
        Assert.Equal(Source, range.SourceId);
        Assert.Equal(skipped.ContentStage.PhysicalityCount, range.RowCount);
    }

    [Fact]
    public void Warm_root_memo_retains_each_native_source_observation()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("ab");
        Hash128 root = ContentTierSpine.ResolveRoot(bytes)!.Value;
        ContentLadderLedger.Begin();
        ContentLadderLedger.MarkPersisted([root]);
        var other = new Hash128(401, 402);
        var builder = NewBuilder("warm-distinct-source");
        foreach (var source in new[] { Source, Source, other })
        {
            Assert.True(ContentTierSpine.TryStageIntoBuilder(builder, bytes, source, out var observed));
            Assert.Equal(root, observed);
        }
        Assert.Equal(1, builder.ContentStage.EntityCount);
        Assert.Equal(3, builder.ContentStage.PhysicalityCount);
        Assert.Equal(new[] { new PhysicalitySourceRange(0, 2, Source),
            new PhysicalitySourceRange(2, 1, other) }, builder.ContentStage.PhysicalitySourceRanges.ToArray());
    }

    [Fact]
    public void Warm_atomic_root_retains_floor_observation_without_entity_or_wrapper()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("a");
        Hash128 root = ContentTierSpine.ResolveRoot(bytes)!.Value;
        ContentLadderLedger.Begin();
        ContentLadderLedger.MarkPersisted([root]);
        var builder = NewBuilder("warm-atomic-source");
        Assert.True(ContentTierSpine.TryStageIntoBuilder(builder, bytes, Source, out var observed));
        Assert.Equal(root, observed);
        Assert.Equal(0, builder.ContentStage.EntityCount);
        Assert.Equal(1, builder.ContentStage.PhysicalityCount);
        Assert.Equal(new PhysicalitySourceRange(0, 1, Source),
            Assert.Single(builder.ContentStage.PhysicalitySourceRanges));
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
    public void End_disarms_but_Begin_keeps_membership_for_warm_reingest()
    {
        var derived = Stage("filter", "baseline");
        ContentLadderLedger.Begin();
        ContentLadderLedger.MarkPersisted([derived]);
        Assert.True(ContentLadderLedger.IsPersisted(derived));

        ContentLadderLedger.End();

        Assert.False(ContentLadderLedger.Armed);
        Assert.False(ContentLadderLedger.IsPersisted(derived));

        // Warm re-arm of the same source: membership survives End.
        ContentLadderLedger.Begin();
        Assert.True(ContentLadderLedger.Armed);
        Assert.True(ContentLadderLedger.IsPersisted(derived));
        Assert.Equal(derived, Stage("filter", "warm"));
    }

    [Fact]
    public void Reset_clears_membership_for_source_change()
    {
        var derived = Stage("filter", "baseline");
        ContentLadderLedger.Begin();
        ContentLadderLedger.MarkPersisted([derived]);
        ContentLadderLedger.Reset();

        Assert.False(ContentLadderLedger.Armed);
        ContentLadderLedger.Begin();
        Assert.False(ContentLadderLedger.IsPersisted(derived));
        Assert.Equal(derived, Stage("filter", "after-reset"));
    }

    [Fact]
    public void Marking_outside_a_run_is_a_no_op()
    {
        var derived = Stage("filter", "baseline");
        ContentLadderLedger.Reset();

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
