namespace Laplace.Engine.Core;

/// <summary>
/// Boolean environment-variable reading. Accepts 1/true/yes/on and their negations,
/// case- and whitespace-insensitive, and takes the default explicitly so opt-in and
/// opt-out flags read the same way.
/// </summary>
public static class EnvFlag
{
    /// <summary>Read <paramref name="name"/> as a boolean, or <paramref name="whenUnset"/>.</summary>
    /// <remarks>
    /// A set-but-unrecognized value also yields <paramref name="whenUnset"/>, so an
    /// unreadable value never flips the flag.
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
