namespace Laplace.Smoke.Tests;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core;
using Laplace.Core.Abstractions;
using Laplace.Decomposers.Math;
using Laplace.Decomposers.Text;
using Laplace.Pipeline;
using Laplace.Pipeline.Abstractions;

using Xunit;

/// <summary>
/// F2 INumberDecomposition + IUnitDecomposition property tests. Both
/// decomposers route through F1 ITextDecomposition — a number literal and
/// the same string as text MUST produce identical composition hashes
/// (content addressing erases the distinction). Cross-modal dedup of the
/// number entity is what makes "how many things intersect with 3.14?"
/// first-class across math/code/text/sensor data.
/// </summary>
public class NumberAndUnitDecomposerTests
{
    [Fact]
    public async Task Number_440_HasIdenticalHashToTextDecomposed_440()
    {
        var (text, _) = MakeText();
        var (number, _) = MakeNumber();

        var textHash   = await text.DecomposeAsync("440", CancellationToken.None);
        var numberHash = await number.DecomposeAsync("440", default, CancellationToken.None);

        AssertHashesEqual(textHash, numberHash);
    }

    [Fact]
    public async Task Number_PI_HasIdenticalHashToTextDecomposed_PI()
    {
        var (text, _) = MakeText();
        var (number, _) = MakeNumber();

        var textHash   = await text.DecomposeAsync("3.14", CancellationToken.None);
        var numberHash = await number.DecomposeAsync("3.14", default, CancellationToken.None);

        AssertHashesEqual(textHash, numberHash);
    }

    [Fact]
    public async Task Number_Negative_HandlesSign()
    {
        var (text, _) = MakeText();
        var (number, _) = MakeNumber();

        var textHash   = await text.DecomposeAsync("-273.15", CancellationToken.None);
        var numberHash = await number.DecomposeAsync("-273.15", default, CancellationToken.None);

        AssertHashesEqual(textHash, numberHash);
    }

    [Fact]
    public async Task Number_Fraction_HandlesSlash()
    {
        var (text, _) = MakeText();
        var (number, _) = MakeNumber();

        var textHash   = await text.DecomposeAsync("1/2", CancellationToken.None);
        var numberHash = await number.DecomposeAsync("1/2", default, CancellationToken.None);

        AssertHashesEqual(textHash, numberHash);
    }

    [Fact]
    public async Task Number_NotANumber_Throws()
    {
        var (number, _) = MakeNumber();
        await Assert.ThrowsAsync<System.ArgumentException>(
            async () => await number.DecomposeAsync("not_a_number", default, CancellationToken.None));
    }

    [Fact]
    public async Task Unit_440Hz_HasIdenticalHashToTextDecomposed_440Hz()
    {
        var (text, _) = MakeText();
        var (unit, _) = MakeUnit();

        var textHash = await text.DecomposeAsync("440Hz", CancellationToken.None);
        var unitHash = await unit.DecomposeAsync("440Hz", default, CancellationToken.None);

        AssertHashesEqual(textHash, unitHash);
    }

    [Fact]
    public void Unit_Split_440Hz_YieldsNumber440AndUnitHz()
    {
        var (n, u) = UnitDecomposer.Split("440Hz");
        Assert.Equal("440", n);
        Assert.Equal("Hz", u);
    }

    [Fact]
    public void Unit_Split_3_14m_YieldsNumber314AndUnitm()
    {
        var (n, u) = UnitDecomposer.Split("3.14m");
        Assert.Equal("3.14", n);
        Assert.Equal("m", u);
    }

    [Fact]
    public void Unit_Split_NumberWithSpace_TrimsLeadingWhitespace()
    {
        var (n, u) = UnitDecomposer.Split("3.14 m");
        Assert.Equal("3.14", n);
        Assert.Equal("m", u);
    }

    [Fact]
    public async Task Unit_BareNumber_StillDecomposes()
    {
        var (text, _) = MakeText();
        var (unit, _) = MakeUnit();
        var bareHash = await text.DecomposeAsync("99", CancellationToken.None);
        var unitHash = await unit.DecomposeAsync("99", default, CancellationToken.None);
        AssertHashesEqual(bareHash, unitHash);
    }

    [Fact]
    public async Task Unit_NoNumber_Throws()
    {
        var (unit, _) = MakeUnit();
        await Assert.ThrowsAsync<System.ArgumentException>(
            async () => await unit.DecomposeAsync("Hz", default, CancellationToken.None));
    }

    private static (TextDecomposer Decomposer, RecordingSink Sink) MakeText()
    {
        var hashing = new IdentityHashing();
        var pool    = new CodepointPool(hashing);
        var sink    = new RecordingSink();
        return (new TextDecomposer(pool, hashing, sink, sink), sink);
    }

    private static (NumberDecomposer Decomposer, RecordingSink Sink) MakeNumber()
    {
        var (text, sink) = MakeText();
        return (new NumberDecomposer(text), sink);
    }

    private static (UnitDecomposer Decomposer, RecordingSink Sink) MakeUnit()
    {
        var (text, sink) = MakeText();
        return (new UnitDecomposer(text), sink);
    }

    private static void AssertHashesEqual(AtomId a, AtomId b)
    {
        var sa = a.AsSpan();
        var sb = b.AsSpan();
        Assert.Equal(sa.Length, sb.Length);
        for (var i = 0; i < sa.Length; ++i) { Assert.True(sa[i] == sb[i]); }
    }

    private sealed class RecordingSink : IEntityEmission, IEntityChildEmission
    {
        public readonly List<EntityRecord>      Entities = new();
        public readonly List<EntityChildRecord> Children = new();
        public ValueTask EmitAsync(EntityRecord record, CancellationToken cancellationToken)
        { Entities.Add(record); return ValueTask.CompletedTask; }
        public ValueTask EmitAsync(EntityChildRecord record, CancellationToken cancellationToken)
        { Children.Add(record); return ValueTask.CompletedTask; }
    }
}
