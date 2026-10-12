using global::Npgsql;

namespace Laplace.Chess.Service;

/// <summary>The PostgreSQL cluster and database actually opened by a chess participant.</summary>
public sealed record ChessDatabaseIdentity(string Cluster, string Database)
{
    public const string ProtocolPrefix = "laplace database ";

    public static ChessDatabaseIdentity Read(NpgsqlDataSource source)
    {
        using var connection = source.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT system_identifier::text, current_database() FROM pg_control_system()";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Content server returned no cluster identity.");
        return new(reader.GetString(0), reader.GetString(1));
    }
}
