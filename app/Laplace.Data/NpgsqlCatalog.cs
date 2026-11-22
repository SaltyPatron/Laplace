using System.Globalization;
using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

/// <summary>
/// Command ownership and binding for catalog-validated PostgreSQL statements.
/// </summary>
public static class NpgsqlCatalog
{
    private static string InstalledOperationSql(
        bool procedure, string qualifiedName,
        IReadOnlyList<(string Name, string Type, NpgsqlParameter Parameter)> arguments, long rowsToFetch)
    {
        var slots = new string[arguments.Count];
        using var identifiers = new NpgsqlCommandBuilder();
        for (int i = 0; i < arguments.Count; i++)
            slots[i] = $"{identifiers.QuoteIdentifier(arguments[i].Name)} => @{arguments[i].Parameter.ParameterName}::{arguments[i].Type}";

        string joined = string.Join(", ", slots);
        return procedure
            ? $"CALL {qualifiedName}({joined})"
            : $"SELECT * FROM {qualifiedName}({joined}) LIMIT {rowsToFetch.ToString(CultureInfo.InvariantCulture)}";
    }

    private static NpgsqlCommand BindInstalledOperationParameters(
        NpgsqlCommand command,
        IReadOnlyList<(string Name, string Type, NpgsqlParameter Parameter)> arguments)
    {
        foreach (var argument in arguments) command.Parameters.Add(argument.Parameter);
        return command;
    }

    internal static NpgsqlCommand InstalledOperationCommand(
        NpgsqlDataSource source, bool procedure, string qualifiedName,
        IReadOnlyList<(string Name, string Type, NpgsqlParameter Parameter)> arguments, long rowsToFetch)
        => BindInstalledOperationParameters(
            source.CreateCommand(InstalledOperationSql(procedure, qualifiedName, arguments, rowsToFetch)),
            arguments);

    internal static NpgsqlCommand InstalledOperationCommand(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, bool procedure, string qualifiedName,
        IReadOnlyList<(string Name, string Type, NpgsqlParameter Parameter)> arguments, long rowsToFetch)
        => BindInstalledOperationParameters(
            new NpgsqlCommand(InstalledOperationSql(procedure, qualifiedName, arguments, rowsToFetch), connection, transaction),
            arguments);

    private static readonly Regex QualifiedRelation = new(
        "^(?:\"(?:\"\"|[^\"])+\"|[a-z_][a-z0-9_$]*)(?:\\.(?:\"(?:\"\"|[^\"])+\"|[a-z_][a-z0-9_$]*))?\\z",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static NpgsqlCommand VacuumCommand(
        NpgsqlDataSource source, string? relation, bool full, bool analyze)
    {
        if (relation is not null && (!QualifiedRelation.IsMatch(relation) || relation.Contains('\0')))
            throw new ArgumentException("Expected a catalog-resolved relation name.", nameof(relation));

        var opts = new List<string>(2);
        if (full) opts.Add("FULL");
        if (analyze) opts.Add("ANALYZE");
        var optSql = opts.Count == 0 ? "" : " (" + string.Join(", ", opts) + ")";
        var text = relation is null ? $"VACUUM{optSql}" : $"VACUUM{optSql} {relation}";
        return source.CreateCommand(text);
    }

    public static NpgsqlCommand Command<TArgument>(NpgsqlDataSource source, string name, TArgument argument)
        => Command(source, name, arguments: [argument]);

    public static NpgsqlCommand Command<TArgument>(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string name, TArgument argument)
        => Command(connection, transaction, name, arguments: [argument]);

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

    public static NpgsqlCommand PreparedCommand(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string name, params object?[] arguments)
    {
        var command = Command(connection, transaction, name, arguments);
        try
        {
            command.Prepare();
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    public static async Task<NpgsqlCommand> PreparedCommandAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string name, CancellationToken ct, params object?[] arguments)
    {
        var command = Command(connection, transaction, name, arguments);
        try
        {
            await command.PrepareAsync(ct).ConfigureAwait(false);
            return command;
        }
        catch
        {
            await command.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static NpgsqlCommand Command(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string name,
        IReadOnlyDictionary<string, string> identifiers, params object?[] arguments)
    {
        var query = SqlCatalog.Get(name);
        string text = query.Text;
        foreach (var (slot, identifier) in identifiers)
        {
            string marker = "{" + slot + "}";
            if (!Regex.IsMatch(slot, "^[a-z_][a-z0-9_]*\\z", RegexOptions.CultureInvariant)
                || !text.Contains(marker, StringComparison.Ordinal)
                || !QualifiedRelation.IsMatch(identifier) || identifier.Contains('\0'))
                throw new ArgumentException("Expected a declared catalog identifier slot.", nameof(identifiers));
            text = text.Replace(marker, identifier, StringComparison.Ordinal);
        }
        if (Regex.IsMatch(text, "\\{[a-z_][a-z0-9_]*\\}", RegexOptions.CultureInvariant))
            throw new ArgumentException("A catalog identifier slot was not supplied.", nameof(identifiers));
        return Bind(new NpgsqlCommand(text, connection, transaction), query, arguments);
    }

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
            if (scalar == "public.blake3")
            {
                if (parameters[i].DataTypeName != declared)
                    throw new ArgumentException($"{query.Name}: parameter {i + 1} must be {declared}.");
                parameters[i].ParameterName = string.Empty;
                continue;
            }
            var expected = scalar switch
            {
                "bytea" => NpgsqlDbType.Bytea,
                "uuid" => NpgsqlDbType.Uuid,
                "int2" => NpgsqlDbType.Smallint,
                "int4" => NpgsqlDbType.Integer,
                "int8" => NpgsqlDbType.Bigint,
                "text" => NpgsqlDbType.Text,
                "jsonb" => NpgsqlDbType.Jsonb,
                "bool" => NpgsqlDbType.Boolean,
                "float8" => NpgsqlDbType.Double,
                "timestamptz" => NpgsqlDbType.TimestampTz,
                _ => throw new InvalidOperationException($"Unknown native parameter type {query.ParameterTypes[i]}."),
            };
            if (array) expected |= NpgsqlDbType.Array;
            if (parameters[i].NpgsqlDbType != expected)
                throw new ArgumentException($"{query.Name}: parameter {i + 1} must be {query.ParameterTypes[i]}.");
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
