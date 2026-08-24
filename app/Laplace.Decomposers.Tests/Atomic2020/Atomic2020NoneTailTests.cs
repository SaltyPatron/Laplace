using System.Text;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Atomic2020.Tests;

/// <summary>
/// ATOMIC2020 states "no tail exists for this head under this relation" as the literal
/// tail "none" -- 147,608 of 1,331,113 rows across train/dev/test, 11.09%. Every one of
/// them used to fold as a CONFIRM toward the entity `none`, which asserts the opposite of
/// what the corpus said: that the head DOES stand in that relation to something. Measured
/// on the 2026-08-23 foundation seed, that was 83,224 live edges.
/// </summary>
public sealed class Atomic2020NoneTailTests
{
    private static Hash128 Split => Hash128.OfCanonical("atomic/split/test");

    private static bool Extract(string line, out RelationTripleRecord record)
        => Atomic2020Decomposer.TryExtract(Encoding.UTF8.GetBytes(line), Split, out record);

    [Fact]
    public void None_Tail_Carries_A_Null_Object_So_The_Spine_Folds_A_Refute()
    {
        Assert.True(Extract("PersonX abandons ___\txNeed\tnone", out var record));
        Assert.Null(record.ObjectCanonical);
    }

    [Fact]
    public void A_Real_Tail_Is_Unaffected()
    {
        Assert.True(Extract("PersonX abandons ___\txNeed\tto grab the bag", out var record));
        Assert.NotNull(record.ObjectCanonical);
        Assert.Equal("to grab the bag", Encoding.UTF8.GetString(record.ObjectCanonical!));
    }

    /// <summary>
    /// The entity `none` is NOT filtered and must never be. It is a real word with a real
    /// content-addressed identity that WordNet and OMW legitimately witness -- and when
    /// ATOMIC names it as a genuine subject it is the SAME id, because identity is content.
    /// Only ATOMIC's TAIL column spells absence that way, which is why the test is on the
    /// column and not on the string.
    /// </summary>
    [Fact]
    public void None_As_A_Subject_Stays_A_Real_Entity()
    {
        Assert.True(Extract("none\txNeed\tto exist", out var record));
        Assert.Equal("none", Encoding.UTF8.GetString(record.SubjectCanonical));
        Assert.NotNull(record.ObjectCanonical);
    }
}
