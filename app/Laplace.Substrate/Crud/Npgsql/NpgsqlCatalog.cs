using global::Npgsql;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Command ownership for catalog operations that participate in an explicit
/// application transaction or need a reader with an application-owned lifetime.
/// Statement text and PostgreSQL parameter types come from the native catalog;
/// arguments are positional values, never SQL or caller-selected identifiers.
/// </summary>
public static class NpgsqlCatalog
{
    public static NpgsqlCommand Command(NpgsqlDataSource source, string name, params object?[] arguments)
    {
        var query = SqlCatalog.Get(name);
        return Bind(source.CreateCommand(query.Text), query, arguments);
    }

    public static NpgsqlCommand Command(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string name, params object?[] arguments)
    {
        var query = SqlCatalog.Get(name);
        return Bind(new NpgsqlCommand(query.Text, connection, transaction), query, arguments);
    }

    private static NpgsqlCommand Bind(NpgsqlCommand command, NativeSqlQuery query, object?[] arguments)
    {
        try
        {
            if (arguments.Length != query.ParameterTypes.Length)
                throw new ArgumentException($"{query.Name}: expected {query.ParameterTypes.Length} arguments, got {arguments.Length}.");
            for (var index = 0; index < arguments.Length; index++)
            {
                // DataTypeName uses the catalog's PostgreSQL type directly. This
                // retains the type of NULL and JSON values without a second enum
                // mapping or Npgsql's named-placeholder rewriting.
                command.Parameters.Add(new NpgsqlParameter
                {
                    DataTypeName = query.ParameterTypes[index],
                    Value = arguments[index] ?? DBNull.Value
                });
            }
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }
}
