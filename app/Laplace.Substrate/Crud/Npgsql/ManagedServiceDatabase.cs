using Laplace.Engine.Core;
using global::Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Database route for managed services: only a local route the host authenticates is
/// accepted, never a TCP/password route from an environment file. On Linux that is the
/// cluster's Unix-domain socket (one absolute directory: <c>/var/run/postgresql</c>, or <c>/tmp</c> on the
/// cluster Laplace-Operations declares; the port the machine declares in <c>LAPLACE_PGPORT</c>, 5432 when it
/// declares none; the host's local HBA/map); on
/// Windows, where PostgreSQL has no peer authentication, it is the loopback host under
/// SSPI (the host's HBA/map; Npgsql negotiates it when no password is given). No password
/// or passfile is allowed on either. This guards the route, not the role's privileges, and the role and database
/// are the installation's own (<c>LAPLACE_DB</c>), as the HBA map decides.
/// </summary>
public static class ManagedServiceDatabase
{
    public static string Resolve(string? connectionString = null)
    {
        const string failure = "Managed services require the installed local host-authenticated database route (peer socket on Linux, SSPI over loopback on Windows); TCP and database credentials are not allowed.";
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
        if (parsed.Port != DeclaredPort() || string.IsNullOrWhiteSpace(parsed.Username) || string.IsNullOrWhiteSpace(parsed.Database)
            || parsed.ShouldSerialize("Password") || parsed.ShouldSerialize("Passfile"))
            throw new InvalidOperationException(failure);
        var hostAuthenticated = OperatingSystem.IsWindows()
            ? parsed.Host is "127.0.0.1" or "localhost" or "::1"
            : IsSocketDirectory(parsed.Host);
        if (!hostAuthenticated)
            throw new InvalidOperationException(failure);
        return parsed.ConnectionString;
    }

    // One absolute directory names a Unix-domain socket: no network route is reached through it. Which directory is
    // the cluster's own: /var/run/postgresql on a distribution's cluster, /tmp on the one Laplace-Operations declares.
    private static bool IsSocketDirectory(string? host) =>
        host is { Length: > 1 } && host[0] == '/' && !host.Contains(',') && !host.Contains("/../") && !host.EndsWith("/..");

    // The port the machine declares for its cluster (LAPLACE_PGPORT); 5432 when it declares none.
    private static int DeclaredPort() =>
        int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_PGPORT"), out var port) && port is > 0 and <= 65535 ? port : 5432;
}
