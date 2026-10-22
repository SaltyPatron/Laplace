using global::Npgsql;
using NpgsqlTypes;
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

    // The catalog name is authoritative even if an externally constructed
    // NativeSqlQuery record carries different text or parameter metadata.
    public static NpgsqlBatchCommand Command(NativeSqlQuery query,
        Action<NpgsqlParameterCollection>? bind = null)
    {
        var contract = SqlCatalog.Get(query.Name);
        var command = new NpgsqlBatchCommand(contract.Text);
        bind?.Invoke(command.Parameters);
        ValidateParameters(contract, command.Parameters);
        return command;
    }

    public static NpgsqlCommand Command(NpgsqlDataSource source, NativeSqlQuery query,
        Action<NpgsqlParameterCollection>? bind = null, int timeoutSeconds = 0)
    {
        var contract = SqlCatalog.Get(query.Name);
        return Bind(source.CreateCommand(contract.Text), contract, bind, timeoutSeconds);
    }

    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        NativeSqlQuery query, Action<NpgsqlParameterCollection>? bind = null, int timeoutSeconds = 0)
    {
        var contract = SqlCatalog.Get(query.Name);
        return Bind(new NpgsqlCommand(contract.Text, connection, transaction), contract, bind, timeoutSeconds);
    }

    private static NpgsqlCommand Bind(NpgsqlCommand command, NativeSqlQuery contract,
        Action<NpgsqlParameterCollection>? bind, int timeoutSeconds)
    {
        try
        {
            if (timeoutSeconds > 0) command.CommandTimeout = timeoutSeconds;
            bind?.Invoke(command.Parameters);
            ValidateParameters(contract, command.Parameters);
            return command;
        }
        catch { command.Dispose(); throw; }
    }

    internal static void ValidateParameters(NativeSqlQuery query, NpgsqlParameterCollection parameters)
    {
        if (parameters.Count != query.ParameterTypes.Length)
            throw new ArgumentException($"{query.Name}: expected {query.ParameterTypes.Length} parameters, got {parameters.Count}.");
        for (var i = 0; i < parameters.Count; i++)
        {
            string declared = query.ParameterTypes[i];
            bool array = declared.EndsWith("[]", StringComparison.Ordinal);
            string scalar = array ? declared[..^2] : declared;
            var expected = scalar switch
            {
                "bytea" => NpgsqlDbType.Bytea,
                "uuid" => NpgsqlDbType.Uuid,
                "int2" => NpgsqlDbType.Smallint,
                "int4" => NpgsqlDbType.Integer,
                "int8" => NpgsqlDbType.Bigint,
                "text" => NpgsqlDbType.Text,
                "bool" => NpgsqlDbType.Boolean,
                "float8" => NpgsqlDbType.Double,
                "timestamptz" => NpgsqlDbType.TimestampTz,
                _ => throw new InvalidOperationException($"Unknown native parameter type {query.ParameterTypes[i]}."),
            };
            if (array) expected |= NpgsqlDbType.Array;
            if (parameters[i].NpgsqlDbType != expected)
                throw new ArgumentException($"{query.Name}: parameter {i + 1} must be {query.ParameterTypes[i]}.");
            // Catalog statements use $1..$n positions. A named parameter would make
            // Npgsql rewrite named placeholders and drop the positional values.
            parameters[i].ParameterName = string.Empty;
        }
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
