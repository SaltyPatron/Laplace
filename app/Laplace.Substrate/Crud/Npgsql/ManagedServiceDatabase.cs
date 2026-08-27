using Laplace.Engine.Core;
using global::Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Managed Linux services never inherit a TCP/password database route from an
/// optional environment file. Authentication still depends on the host's peer
/// HBA/map; this is a connection-route guard, not a least-privilege role claim.
/// STDIO, CLI and existing Windows clients retain their installed configuration.
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
