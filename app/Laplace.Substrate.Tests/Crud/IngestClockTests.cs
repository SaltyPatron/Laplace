using Xunit;
using Laplace.SubstrateCRUD;

namespace Laplace.SubstrateCRUD.Tests;

public class IngestClockTests
{
    [Fact]
    public void ParseEpoch_NullOrWhitespace_IsNotDeterministic()
    {
        Assert.Null(IngestClock.ParseEpoch(null));
        Assert.Null(IngestClock.ParseEpoch(""));
        Assert.Null(IngestClock.ParseEpoch("   "));
    }

    [Fact]
    public void ParseEpoch_IntegerLiteral_IsExactUnixMicroseconds()
    {
        Assert.Equal(1_700_000_000_123_456L, IngestClock.ParseEpoch("1700000000123456"));
        // Tolerate surrounding whitespace (env vars can pick this up from shells).
        Assert.Equal(42L, IngestClock.ParseEpoch("  42  "));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("on")]
    [InlineData("yes")]
    [InlineData("not-a-number")]
    [InlineData("2024-03-15T12:00:00Z")] // deliberately NOT ISO-8601-aware -- see ParseEpoch doc
    public void ParseEpoch_OtherTruthyValue_FallsBackToGenesisSentinel(string raw)
    {
        Assert.Equal(IngestClock.GenesisEpochUnixUs, IngestClock.ParseEpoch(raw));
    }

    [Fact]
    public void ParseEpoch_GenesisSentinel_MatchesNativeMirror()
    {
        // Must stay byte-identical to kDeterministicGenesisUs in attestation_engine.c —
        // 2020-01-01T00:00:00Z in microseconds. If this assertion breaks, the native
        // constant needs updating too, or the two clocks diverge under the truthy-sentinel path.
        long computed = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000L;
        Assert.Equal(IngestClock.GenesisEpochUnixUs, computed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void ParseEpoch_ZeroOrNegativeInteger_FallsBackToGenesisSentinel(string raw)
    {
        // Must match attestation_engine.c's `parsed > 0` gate: 0/negative can't be
        // distinguished from "unset" through the C int64_t sentinel there, so both clocks
        // collapse it to the genesis sentinel rather than the literal value.
        Assert.Equal(IngestClock.GenesisEpochUnixUs, IngestClock.ParseEpoch(raw));
    }
}
