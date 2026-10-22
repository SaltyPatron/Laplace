using System.Globalization;

namespace Laplace.Ops;

internal sealed record PersistentLogRetention(long FileBytes, int RetainedFiles)
{
    internal static PersistentLogRetention FromEnvironment()
        => new(Read("LAPLACE_LOG_FILE_BYTES", 50 * 1024 * 1024, long.MaxValue),
            (int)Read("LAPLACE_LOG_RETAINED_FILES", 31, int.MaxValue));

    private static long Read(string name, long fallback, long maximum)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (value is null) return fallback;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
            || parsed < 1 || parsed > maximum)
            throw new InvalidOperationException($"{name} must be a positive integer no larger than {maximum}.");
        return parsed;
    }
}
