using System.Text;
using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Atomic2020.Tests;

/// <summary>
/// ATOMIC2020 spells "this head has no tail under this relation" as the literal tail
/// <c>none</c>. Extraction turns that tail into a null object so the shared fold scores
/// the testimony as a refutation of the relation, not a confirmation toward the word.
/// </summary>
public sealed class Atomic2020NoneTailTests
{
    private static bool Extract(string line, out RelationTripleRecord record)
        => Atomic2020Decomposer.TryExtract(Encoding.UTF8.GetBytes(line), "test", out record);

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
    /// The word <c>none</c> keeps its content identity: only the tail column spells
    /// absence, so <c>none</c> as a subject is the same entity every other source witnesses.
    /// </summary>
    [Fact]
    public void None_As_A_Subject_Stays_A_Real_Entity()
    {
        Assert.True(Extract("none\txNeed\tto exist", out var record));
        Assert.Equal("none", Encoding.UTF8.GetString(record.SubjectCanonical));
        Assert.NotNull(record.ObjectCanonical);
    }
}
