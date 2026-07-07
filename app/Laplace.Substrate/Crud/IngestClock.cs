using System.Globalization;

namespace Laplace.SubstrateCRUD;

/// <summary>
/// Central ingest-time source for the decomposer/build path. Live ingest uses the wall clock
/// (behavior unchanged). Package builds set <c>LAPLACE_DETERMINISTIC_TIME</c> before touching
/// any decomposer/native code so the produced artifact bytes are byte-stable across builds —
/// the same "build twice, byte-compare" determinism model the T0/highway perfcaches already
/// enforce (<c>laplace_verify_perfcache_determinism</c>, which compares two separate process
/// invocations), extended to observation packages.
///
/// <para>
/// The env var is the single source of truth and is consulted exactly ONCE per process, on
/// both sides of the managed/native boundary: here (a <c>static readonly</c> field) and in
/// <c>engine/core/src/attestation_engine.c</c>'s <c>laplace_deterministic_time_us()</c> (a
/// function-local cache). A process-scoped, read-once env var is the only channel both sides
/// can observe consistently — an in-process-only override (e.g. an AsyncLocal pin) would be
/// invisible to native attestation timestamps and silently defeat the determinism gate for
/// exactly the rows it checks. Do not add one without a matching native channel.
/// </para>
///
/// <para>
/// The values threaded through here are evidence columns (physicality.observed_at,
/// attestation.last_observed_at, <c>SubstrateChangeMetadata.BuiltAt</c>), never part of the
/// content-addressed id (BLAKE3 of the canonical form). Pinning them therefore changes
/// artifact bytes only, never entity identity.
/// </para>
/// </summary>
public static class IngestClock
{
    /// <summary>
    /// Canonical build-time sentinel epoch used when <c>LAPLACE_DETERMINISTIC_TIME</c> is set to a
    /// truthy-but-non-numeric value: 2020-01-01T00:00:00Z, in microseconds since the Unix epoch.
    /// Mirrors <c>kDeterministicGenesisUs</c> in attestation_engine.c.
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
    /// Parses the <c>LAPLACE_DETERMINISTIC_TIME</c> value convention: a strictly-positive
    /// integer is taken as exact microseconds since the Unix epoch; any other non-empty value
    /// (e.g. "1", "true", "0", a negative number) maps to <see cref="GenesisEpochUnixUs"/>;
    /// null/empty/whitespace means "not set" (<see langword="null"/>). Deliberately narrow —
    /// NOT ISO-8601-aware, even though <see cref="DateTimeOffset.TryParse(string?,out DateTimeOffset)"/>
    /// could do it trivially here: <c>attestation_engine.c</c>'s C-side twin only ever attempts
    /// <c>strtoll</c>, with no date-string parsing (no portable strptime on the Windows/icx
    /// build), so any format this managed parser accepted that the C side rejected would
    /// silently diverge the two clocks for that input. The contract is kept to the exact
    /// intersection of what both sides can parse identically. Pure and side-effect-free so it
    /// can be unit-tested without process-env timing (the live field below is cached once at
    /// type load — process-scoped by design, matching the native cache-once-per-process model).
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
