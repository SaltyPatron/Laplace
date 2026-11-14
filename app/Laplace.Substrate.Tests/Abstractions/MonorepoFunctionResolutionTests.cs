using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests.Abstractions;

/// <summary>
/// Every call to a function in a schema the monorepo's laplace_substrate extension owns resolves to a function the
/// extension (or the declared schema) still defines. PL/pgSQL and SQL-standard bodies are bound only when they run or
/// when the schema is applied, so a family retired onto the Native substrate while another body or catalog query still
/// calls it fails at deploy (as `laplace.word_id` did inside `consensus.edges` and `laplace.source_id` inside
/// `ops.source_status`), never at build. This reads the source instead: calls in the extension's SQL, the declared
/// schema and the SQL catalog, against definitions in the extension's SQL and the declared schema.
/// </summary>
public sealed class MonorepoFunctionResolutionTests
{
    private static readonly string[] OwnedSchemas =
        ["laplace", "consensus", "converse", "generation", "lexical", "ops", "realize", "structural", "taxonomy"];

    private static readonly Regex Comment = new(@"--[^\n]*", RegexOptions.Compiled);
    // An unqualified name in the extension's script or the declared schema is created in laplace (the extension's
    // schema; the declared schema runs with search_path laplace,public).
    private static readonly Regex Definition = new(
        @"CREATE\s+(?:OR\s+REPLACE\s+)?(?:FUNCTION|PROCEDURE|AGGREGATE)\s+(?:(?<schema>[@\w]+)\.)?(?<name>\w+)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Relation = new(
        @"CREATE\s+(?:OR\s+REPLACE\s+)?(?:UNLOGGED\s+)?(?:FOREIGN\s+TABLE|TABLE|MATERIALIZED\s+VIEW|VIEW|TYPE|DOMAIN)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?<schema>[@\w]+)\.)?(?<name>\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Call = new(@"(?<![\w.])(?<schema>[@\w]+)\.(?<name>\w+)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void EveryCallIntoAnOwnedSchemaResolves()
    {
        var root = RepoRoot();
        var extension = Directory.GetFiles(Path.Combine(root, "extension", "laplace_substrate", "sql"), "*.sql.in",
            SearchOption.AllDirectories);
        var schema = Directory.GetFiles(Path.Combine(root, "db", "schema"), "*.sql");
        var catalog = Directory.GetFiles(Path.Combine(root, "engine", "core", "src", "sql_catalog"), "*.def");

        var functions = new HashSet<string>(StringComparer.Ordinal);
        var relations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in extension.Concat(schema))
        {
            var text = Comment.Replace(File.ReadAllText(file), "");
            foreach (Match m in Definition.Matches(text)) functions.Add(Key(m));
            foreach (Match m in Relation.Matches(text)) relations.Add(Key(m));
        }
        Assert.True(functions.Count > 100, $"read only {functions.Count} function definitions");

        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in extension.Concat(schema).Concat(catalog))
        {
            var text = Comment.Replace(File.ReadAllText(file), "");
            foreach (Match m in Call.Matches(text))
            {
                var key = Key(m);
                if (!OwnedSchemas.Contains(key[..key.IndexOf('.')])) continue;
                if (functions.Contains(key) || relations.Contains(key)) continue;   // a relation's column list
                unresolved.Add($"{key} in {Path.GetRelativePath(root, file).Replace('\\', '/')}");
            }
        }
        Assert.True(unresolved.Count == 0,
            "calls to functions no longer defined (retired onto Native? call the Native function instead):\n  "
            + string.Join("\n  ", unresolved));
    }

    private static string Key(Match m)
    {
        var schema = m.Groups["schema"].Success ? m.Groups["schema"].Value.ToLowerInvariant() : "laplace";
        return (schema == "@extschema@" ? "laplace" : schema) + "." + m.Groups["name"].Value.ToLowerInvariant();
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "extension", "laplace_substrate", "sql"))) return dir.FullName;
        var source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", ".."));
        return Directory.Exists(Path.Combine(source, "extension", "laplace_substrate", "sql"))
            ? source : throw new DirectoryNotFoundException("extension/laplace_substrate/sql");
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
