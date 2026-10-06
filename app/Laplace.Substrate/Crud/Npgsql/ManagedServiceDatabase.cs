using Laplace.Engine.Core;
using global::Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Database route for managed services: only a local route the host authenticates is
/// accepted, never a TCP/password route from an environment file. On Linux that is the
/// peer socket (<c>/var/run/postgresql</c>, port 5432, the host's peer HBA/map); on
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
        if (parsed.Port != 5432 || string.IsNullOrWhiteSpace(parsed.Username) || string.IsNullOrWhiteSpace(parsed.Database)
            || parsed.ShouldSerialize("Password") || parsed.ShouldSerialize("Passfile"))
            throw new InvalidOperationException(failure);
        var hostAuthenticated = OperatingSystem.IsWindows()
            ? parsed.Host is "127.0.0.1" or "localhost" or "::1"
            : parsed.Host == "/var/run/postgresql";
        if (!hostAuthenticated)
            throw new InvalidOperationException(failure);
        return parsed.ConnectionString;
    }
}
