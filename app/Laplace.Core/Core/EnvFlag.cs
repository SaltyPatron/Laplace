namespace Laplace.Engine.Core;

/// <summary>
/// One reading of a boolean environment variable.
///
/// Before this there were four, and they disagreed on what "on" means:
/// CpuTopology trimmed and lowercased then matched 1/true/yes; NpgsqlIndexCycle
/// matched only the literals "1"/"true"/"TRUE", so LAPLACE_INDEX_CYCLE_DEFER=True
/// or a trailing space silently left the index bracket OFF with no error — on the
/// exact flag a seed campaign depends on for its fast path; CopyBlobValidator
/// tested != "0", which makes any typo mean "on" for an opt-OUT flag.
///
/// The disagreement is the defect, not any one spelling. This accepts
/// 1/true/yes/on and their negations, case- and whitespace-insensitive, and
/// takes the default explicitly so opt-in and opt-out flags read the same way.
/// </summary>
public static class EnvFlag
{
    /// <summary>Read <paramref name="name"/> as a boolean, or <paramref name="whenUnset"/>.</summary>
    /// <remarks>
    /// A set-but-unrecognized value also yields <paramref name="whenUnset"/>: an
    /// operator who wrote something we cannot read did not ask to flip the flag.
    /// </remarks>
    public static bool IsSet(string name, bool whenUnset = false)
        => Parse(Environment.GetEnvironmentVariable(name), whenUnset);

    /// <summary>The parse itself, exposed for callers holding the string already.</summary>
    public static bool Parse(string? value, bool whenUnset = false)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return whenUnset;
        return v.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => whenUnset,
        };
    }
}
