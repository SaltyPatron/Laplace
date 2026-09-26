using Laplace.Engine.Core;
using global::Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Database route for managed services: only the local peer socket route
/// (<c>/var/run/postgresql</c>:5432, <c>laplace_admin</c>, database <c>laplace</c>, no
/// password or passfile) is accepted, so a TCP/password route from an environment file
/// is refused. Authentication is the host's peer HBA/map; this guards the route, not
/// the role's privileges.
/// </summary>
public static class ManagedServiceDatabase
{
    public static string Resolve(string? connectionString = null)
    {
        const string failure = "Managed services require the installed local peer database route; TCP and database credentials are not allowed.";
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
        if (parsed.Host != "/var/run/postgresql" || parsed.Port != 5432
            || parsed.Username != "laplace_admin" || parsed.Database != "laplace"
            || parsed.ShouldSerialize("Password") || parsed.ShouldSerialize("Passfile"))
            throw new InvalidOperationException(failure);
        return parsed.ConnectionString;
    }
}
