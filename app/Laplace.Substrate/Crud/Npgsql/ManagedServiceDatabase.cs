using Laplace.Engine.Core;
using global::Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Uses the installation's content route independently of the worker's execution host.
/// Local sockets and loopback connections retain host authentication; remote database
/// connections require certificate and hostname verification. Credentials come from the
/// installed workload configuration, not from a caller's HTTP request.
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
            // Parser errors can include configuration values. Never retain them
            // as an inner exception in a startup log.
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

    // One absolute directory names a Unix-domain socket: no network route is reached through it. Which directory is
    // the cluster's own: /var/run/postgresql on a distribution's cluster, /tmp on the one Laplace-Operations declares.
    private static bool IsSocketDirectory(string? host) =>
        host is { Length: > 1 } && host[0] == '/' && !host.Contains(',') && !host.Contains("/../") && !host.EndsWith("/..");

}
