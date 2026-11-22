using Laplace.Engine.Core;
using Npgsql;

namespace Laplace.Data;

/// <summary>
/// Resolves managed-service database routes using installation config, requiring
/// hostname/certificate verification on non-loopback TCP connections.
/// </summary>
public static class ManagedServiceDatabase
{
    public static string Resolve(string? connectionString = null)
    {
        const string failure = "Managed services require a configured host, role and database; remote PostgreSQL routes require SSL Mode=VerifyFull.";
        NpgsqlConnectionStringBuilder parsed;
        try
        {
            parsed = new NpgsqlConnectionStringBuilder(connectionString ?? LaplaceInstall.PostgresConnectionString());
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(failure);
        }
        if (string.IsNullOrWhiteSpace(parsed.Host) || string.IsNullOrWhiteSpace(parsed.Username)
            || string.IsNullOrWhiteSpace(parsed.Database))
            throw new InvalidOperationException(failure);
        foreach (var host in parsed.Host.Split(',', StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException(failure);
            if (host.StartsWith('/'))
            {
                if (OperatingSystem.IsWindows() || !IsSocketDirectory(host))
                    throw new InvalidOperationException(failure);
            }
            else if (host is not ("127.0.0.1" or "localhost" or "::1") && parsed.SslMode != SslMode.VerifyFull)
                throw new InvalidOperationException(failure);
        }
        return parsed.ConnectionString;
    }

    private static bool IsSocketDirectory(string? host) =>
        host is { Length: > 1 } && host[0] == '/' && !host.Contains(',') && !host.Contains("/../") && !host.EndsWith("/..");
}
