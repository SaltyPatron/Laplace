using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Migrations;

/// <summary>
/// The database's schema, roles and grants are ONE declaration: the SQL files under db/schema,
/// applied in file-name order on every create and every deploy. Nothing is journaled and nothing
/// runs once. Each statement is idempotent (CREATE ... IF NOT EXISTS, CREATE OR REPLACE, grants
/// that read the live catalog), so applying the declaration reconciles a stale grant or a missing
/// object every time. Status applies the declaration inside a transaction it rolls back and reports
/// what would change.
/// </summary>
internal static class DeclaredSchema
{
    /// <summary>The file that declares the Native substrate; the canonical Native step follows it.</summary>
    internal const string SubstrateFile = "10_substrate.sql";

    internal static string LocateDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "db", "schema");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "db", "schema");
        if (Directory.Exists(cwdCandidate)) return cwdCandidate;
        throw new DirectoryNotFoundException("Could not locate db/schema/ — run from the repository root or a published runtime.");
    }

    /// <summary>The declaration files selected by <paramref name="filter"/>, in apply order.</summary>
    internal static IReadOnlyList<string> Files(Func<string, bool> filter)
    {
        var directory = LocateDirectory();
        Console.WriteLine($"Declared schema directory: {directory}");
        var files = Directory.EnumerateFiles(directory, "*.sql").Where(filter)
            .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal).ToList();
        if (files.Count == 0) throw new InvalidOperationException($"No declared schema files match in {directory}.");
        return files;
    }

    private static NpgsqlConnection OpenMaintenance(string connectionString, out string name)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        name = target.Database ?? throw new InvalidOperationException("Database name is required.");
        var maintenance = new NpgsqlConnectionStringBuilder(target.ConnectionString) { Database = "postgres" };
        var connection = new NpgsqlConnection(maintenance.ConnectionString);
        connection.Open();
        return connection;
    }

    internal static bool DatabaseExists(string connectionString)
    {
        using var connection = OpenMaintenance(connectionString, out var name);
        using var exists = NpgsqlCatalog.Command(connection, null, "migration.database_exists", name);
        return exists.ExecuteScalar() is true;
    }

    /// <summary>Create the database when it does not exist; the declaration then fills it.</summary>
    internal static void EnsureDatabase(string connectionString)
    {
        using var connection = OpenMaintenance(connectionString, out var name);
        using (var exists = NpgsqlCatalog.Command(connection, null, "migration.database_exists", name))
            if (exists.ExecuteScalar() is true) return;
        Console.WriteLine($"Creating database \"{name}\".");
        using var create = new NpgsqlCommand(
            DatabaseRecreation.Statement("migration.database_create",
                ("database", DatabaseRecreation.Identifier(name)), ("options", "")),
            connection);
        create.ExecuteNonQuery();
    }

    /// <summary>
    /// Apply every file inside <paramref name="transaction"/>. After the substrate file the canonical
    /// Native step runs (extension update, function bindings, cache paths, staging), so the grants
    /// that follow see the extension's current tables.
    /// </summary>
    internal static void Apply(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<string> files, Action<string>? applied = null)
    {
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            using (var command = new NpgsqlCommand(File.ReadAllText(file), connection, transaction) { CommandTimeout = 0 })
                command.ExecuteNonQuery();
            if (string.Equals(name, SubstrateFile, StringComparison.Ordinal)) CanonicalSubstrate(connection, transaction);
            applied?.Invoke(name);
        }
    }

    private static void CanonicalSubstrate(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        using (var command = NpgsqlCatalog.Command(connection, transaction, "migration.canonical_substrate"))
        {
            command.CommandTimeout = 0;
            command.ExecuteNonQuery();
        }
        using var status = NpgsqlCatalog.Command(connection, transaction, "migration.canonical_substrate_status");
        using var receipt = status.ExecuteReader();
        if (!receipt.Read())
            throw new InvalidOperationException("Canonical Native substrate verification returned no receipt.");
        Console.WriteLine($"Canonical Native substrate verified: database={receipt.GetString(0)} extension={receipt.GetString(1)} " +
            $"tier0={receipt.GetString(2)} highway={receipt.GetString(3)}");
    }

    /// <summary>Apply the declaration atomically and commit. Returns the names of the files applied.</summary>
    internal static IReadOnlyList<string> Apply(string connectionString, IReadOnlyList<string> files)
    {
        var applied = new List<string>();
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        Apply(connection, transaction, files, applied.Add);
        transaction.Commit();
        return applied;
    }

    internal enum Kind { Missing, Differs, Removed }

    internal sealed record Difference(Kind Kind, string Key);

    /// <summary>
    /// What applying the declaration would change in the live catalog: the declaration is applied
    /// inside a transaction that is always rolled back, and the catalog is compared before and after.
    /// </summary>
    internal static IReadOnlyList<Difference> Drift(string connectionString, IReadOnlyList<string> files)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var limit = new NpgsqlCommand("SET LOCAL lock_timeout = '15s'", connection, transaction)) limit.ExecuteNonQuery();
            var before = Snapshot(connection, transaction);
            Apply(connection, transaction, files);
            var after = Snapshot(connection, transaction);
            var differences = new List<Difference>();
            foreach (var (key, declared) in after)
            {
                if (!before.TryGetValue(key, out var live)) differences.Add(new(Kind.Missing, key));
                else if (!string.Equals(live, declared, StringComparison.Ordinal)) differences.Add(new(Kind.Differs, key));
            }
            foreach (var key in before.Keys)
                if (!after.ContainsKey(key)) differences.Add(new(Kind.Removed, key));
            return differences.OrderBy(static d => d.Key, StringComparer.Ordinal).ToList();
        }
        finally
        {
            transaction.Rollback();
        }
    }

    private static Dictionary<string, string> Snapshot(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        Read(connection, transaction, "migration.declared_snapshot", rows);
        using (var present = NpgsqlCatalog.Command(connection, transaction, "migration.declaration_present", "app.platform_roles"))
            if (present.ExecuteScalar() is true)
                Read(connection, transaction, "migration.declared_seed_snapshot", rows);
        return rows;
    }

    private static void Read(NpgsqlConnection connection, NpgsqlTransaction transaction, string query, Dictionary<string, string> rows)
    {
        using var command = NpgsqlCatalog.Command(connection, transaction, query);
        command.CommandTimeout = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var value = reader.IsDBNull(1) ? "" : reader.GetString(1);
            // Duplicate keys (for example overloads rendered alike) fold into one comparable value.
            rows[key] = rows.TryGetValue(key, out var existing) ? existing + "|" + value : value;
        }
    }

    /// <summary>Print the drift report; returns true when the live catalog matches the declaration.</summary>
    internal static bool Report(string label, IReadOnlyList<Difference> differences)
    {
        if (differences.Count == 0)
        {
            Console.WriteLine($"[status] {label}: in sync with the declared schema (applying it would change nothing).");
            return true;
        }
        Console.WriteLine($"[status] {label}: DRIFT, applying the declared schema would change {differences.Count} catalog entr{(differences.Count == 1 ? "y" : "ies")}:");
        foreach (var difference in differences.Take(60))
        {
            var mark = difference.Kind switch { Kind.Missing => "+ missing", Kind.Differs => "~ differs", _ => "- removed" };
            Console.WriteLine($"  {mark}  {difference.Key}");
        }
        if (differences.Count > 60) Console.WriteLine($"  ... and {differences.Count - 60} more");
        Console.WriteLine("Run the matching up command (migrate or repair) to apply the declaration.");
        return false;
    }
}
