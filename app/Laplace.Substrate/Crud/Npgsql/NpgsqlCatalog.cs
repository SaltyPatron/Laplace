using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using System.Text.RegularExpressions;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Command ownership for catalog operations that participate in an explicit
/// application transaction or need a reader with an application-owned lifetime.
/// Statement text and PostgreSQL parameter types come from the native catalog;
/// arguments are positional values. Utility relation names fill validated catalog slots.
/// </summary>
public static class NpgsqlCatalog
{
    internal static NpgsqlCommand InstalledOperationCommand(
        NpgsqlDataSource source, bool procedure, string qualifiedName,
        IReadOnlyList<(string Name, string Type, NpgsqlParameter Parameter)> arguments, long rowsToFetch)
    {
        var argumentTemplate = SqlCatalog.Get("operation.argument").Text;
        using var identifiers = new NpgsqlCommandBuilder();
        var slots = new string[arguments.Count];
        for (int i = 0; i < arguments.Count; i++)
            slots[i] = argumentTemplate
                .Replace("{name}", identifiers.QuoteIdentifier(arguments[i].Name), StringComparison.Ordinal)
                .Replace("{slot}", arguments[i].Parameter.ParameterName, StringComparison.Ordinal)
                .Replace("{type}", arguments[i].Type, StringComparison.Ordinal);
        var query = SqlCatalog.Get(procedure ? "operation.procedure" : "operation.function");
        var text = query.Text
            .Replace("{operation}", qualifiedName, StringComparison.Ordinal)
            .Replace("{arguments}", string.Join(", ", slots), StringComparison.Ordinal)
            .Replace("{rows}", rowsToFetch.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var command = source.CreateCommand(text);
        foreach (var argument in arguments) command.Parameters.Add(argument.Parameter);
        return command;
    }

    private static readonly Regex QualifiedRelation = new(
        "^(?:\"(?:\"\"|[^\"])+\"|[a-z_][a-z0-9_$]*)(?:\\.(?:\"(?:\"\"|[^\"])+\"|[a-z_][a-z0-9_$]*))?\\z",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static NpgsqlCommand VacuumCommand(
        NpgsqlDataSource source, string? relation, bool full, bool analyze)
    {
        // VACUUM accepts relation identifiers rather than parameter values. Only a
        // regclass spelling (one or two quoted/unquoted identifiers) may fill this
        // catalog slot; no caller supplies a statement or a command option.
        if (relation is not null && (!QualifiedRelation.IsMatch(relation) || relation.Contains('\0')))
            throw new ArgumentException("Expected a catalog-resolved relation name.", nameof(relation));
        var mode = full ? "full" : analyze ? "analyze" : "plain";
        var query = SqlCatalog.Get("maintenance.vacuum_" + mode + (relation is null ? "" : "_relation"));
        var text = relation is null ? query.Text : query.Text.Replace("{relation}", relation, StringComparison.Ordinal);
        return source.CreateCommand(text);
    }

    // A reference-type array is one PostgreSQL argument, not the params list.
    // The typed overload wins for byte[][]/string[] and retains that array as a value.
    public static NpgsqlCommand Command<TArgument>(NpgsqlDataSource source, string name, TArgument argument)
        => Command(source, name, arguments: new object?[] { argument });

    public static NpgsqlCommand Command<TArgument>(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string name, TArgument argument)
        => Command(connection, transaction, name, arguments: new object?[] { argument });

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

    /// <summary>One round trip for fixed catalog utilities that have no parameters.</summary>
    internal static NpgsqlCommand Command(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0) throw new ArgumentException("Select at least one catalog command.", nameof(names));
        var statements = names.Select(SqlCatalog.Get).ToArray();
        if (statements.Any(query => query.ParameterTypes.Length != 0))
            throw new ArgumentException("Parameterized catalog commands require their own binding.", nameof(names));
        return new NpgsqlCommand(string.Join(";\n", statements.Select(query => query.Text.TrimEnd().TrimEnd(';'))),
            connection, transaction);
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

    internal static string BinaryCopyStatement(IntentStageTable table)
    {
        string name = table switch
        {
            IntentStageTable.Entities => "write.copy_entities",
            IntentStageTable.Physicalities => "write.copy_physicalities",
            IntentStageTable.Attestations => "write.copy_attestations",
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        string text = SqlCatalog.Get(name).Text;
        int first = text.IndexOf('('), last = text.IndexOf(") FROM", StringComparison.Ordinal);
        string columns = Regex.Replace(text[(first + 1)..last], @"\s+", "");
        if (columns != Regex.Replace(IntentStage.CopyColumnList(table), @"\s+", ""))
            throw new InvalidOperationException("Native COPY columns disagree with the catalog contract.");
        return text;
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
