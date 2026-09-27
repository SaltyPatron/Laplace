using System.Globalization;

namespace Laplace.SubstrateCRUD;

/// <summary>
/// Ingest-time clock for the observation stamps an admitted working set carries
/// (physicality.observed_at, attestation.last_observed_at,
/// <c>SubstrateChangeMetadata.BuiltAt</c>). These are evidence columns, never part of a
/// content-addressed id, so a pinned epoch changes artifact bytes and never entity identity.
///
/// <para>
/// The managed epoch is fixed at type load and is unset here, so <see cref="NowUnixUs"/>
/// is the wall clock. The native side (<c>laplace_deterministic_time_us()</c> in
/// <c>attestation_engine.c</c>) caches its epoch once per process; an in-process override
/// the native side cannot see would give managed and native stamps different clocks.
/// </para>
/// </summary>
public static class IngestClock
{
    /// <summary>
    /// Sentinel epoch for a <c>LAPLACE_DETERMINISTIC_TIME</c> value that is set but not a
    /// strictly-positive integer: 2020-01-01T00:00:00Z in microseconds since the Unix epoch.
    /// Equal to <c>kDeterministicGenesisUs</c> in attestation_engine.c.
    /// </summary>
    public const long GenesisEpochUnixUs = 1_577_836_800_000_000L;

    private static readonly long? EnvEpochUs = null;

    /// <summary>True when a deterministic epoch is in effect for this process.</summary>
    public static bool IsDeterministic => EnvEpochUs is not null;

    /// <summary>Current ingest time in microseconds since the Unix epoch.</summary>
    public static long NowUnixUs()
        => EnvEpochUs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

    /// <summary>Current ingest time as a <see cref="DateTimeOffset"/> (e.g. for BuiltAt metadata).</summary>
    public static DateTimeOffset Now()
        => DateTimeOffset.FromUnixTimeMilliseconds(NowUnixUs() / 1000L);

    /// <summary>
    /// Parses the <c>LAPLACE_DETERMINISTIC_TIME</c> convention: a strictly-positive base-10
    /// integer is exact microseconds since the Unix epoch; any other non-empty value
    /// (e.g. "true", "0", a negative number) is <see cref="GenesisEpochUnixUs"/>;
    /// null/empty/whitespace is <see langword="null"/> (not set). No date-string formats are
    /// accepted because the native parser is <c>strtoll</c> only; a format accepted here and
    /// rejected there would give the two sides different clocks for the same input.
    /// </summary>
    internal static long? ParseEpoch(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();

        // Must match attestation_engine.c's `strtoll(...) ... parsed > 0` gate exactly:
        // the whole trimmed string is a base-10 integer, and it's strictly positive.
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us > 0)
            return us;

        return GenesisEpochUnixUs;
    }
}
