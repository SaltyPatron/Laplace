using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Laplace.Engine.Core;

public sealed record NativeSqlQuery(string Name, string Text, string[] ParameterTypes);

/// <summary>Immutable SQL and parameter contracts marshalled from the native catalog.</summary>
public static partial class SqlCatalog
{
    [LibraryImport("laplace_core", EntryPoint = "laplace_sql_query_text", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr TextPointer(string name);

    [LibraryImport("laplace_core", EntryPoint = "laplace_sql_query_parameters", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr ParametersPointer(string name);

    private static readonly Lazy<IReadOnlyDictionary<string, NativeSqlQuery>> EmbeddedFallback = new(LoadEmbeddedCatalog);

    public static NativeSqlQuery Get(string name)
    {
        try
        {
            var text = Marshal.PtrToStringUTF8(TextPointer(name))
                ?? throw new ArgumentException($"Unknown native SQL query '{name}'.", nameof(name));
            var parameters = Marshal.PtrToStringUTF8(ParametersPointer(name))
                ?? throw new InvalidOperationException($"Missing native SQL parameter contract for '{name}'.");
            return new(name, text, parameters.Length == 0 ? [] : parameters.Split(','));
        }
        catch (DllNotFoundException)
        {
            if (EmbeddedFallback.Value.TryGetValue(name, out var query))
                return query;
            throw new ArgumentException($"Unknown native SQL query '{name}'.", nameof(name));
        }
    }

    private static IReadOnlyDictionary<string, NativeSqlQuery> LoadEmbeddedCatalog()
    {
        using var stream = typeof(SqlCatalog).Assembly.GetManifestResourceStream("Laplace.SqlCatalog.def")
            ?? throw new InvalidOperationException("Missing embedded Laplace.SqlCatalog.def resource.");
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();
        var literal = new Regex("\"(?:\\\\.|[^\"\\\\])*\"", RegexOptions.Compiled);
        var pattern = new Regex(
            "SQL_QUERY\\(\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"(?<parameters>[^\"]*)\"\\s*,\\s*(?<text>(?:\"(?:\\\\.|[^\"\\\\])*\"\\s*)+)\\)",
            RegexOptions.Compiled);
        var map = new Dictionary<string, NativeSqlQuery>(StringComparer.Ordinal);
        foreach (Match match in pattern.Matches(source))
        {
            var qName = match.Groups["name"].Value;
            var qParams = match.Groups["parameters"].Value;
            var parts = literal.Matches(match.Groups["text"].Value)
                .Select(m => Regex.Unescape(m.Value[1..^1]));
            var qText = string.Concat(parts);
            map[qName] = new NativeSqlQuery(qName, qText, qParams.Length == 0 ? [] : qParams.Split(','));
        }
        return map;
    }
}
