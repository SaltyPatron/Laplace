using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;







internal static partial class GoldenJson
{
    [GeneratedRegex("^(chatcmpl|cmpl|audit|viz|explain|entity|export|neighbors|members|peers|containers)-[0-9a-f]{32}$")]
    private static partial Regex ResponseIdRegex();

    [GeneratedRegex("^q_[0-9a-f]{32}$")]
    private static partial Regex QuoteIdRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}")]
    private static partial Regex TimestampRegex();

    // Server-minted conversation session keys (spec 34): "s-" + 32 hex.
    [GeneratedRegex("^s-[0-9a-f]{32}$")]
    private static partial Regex SessionKeyRegex();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string GoldensDir([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Goldens");


    public static void Match(string name, string actualJson)
    {
        var node = JsonNode.Parse(actualJson);
        MatchNode(name, node);
    }


    public static void MatchNode(string name, JsonNode? node)
    {
        Normalize(node);
        var actual = (node?.ToJsonString(SerializerOptions) ?? "null")
            .ReplaceLineEndings("\n") + "\n";

        var dir = GoldensDir();
        Directory.CreateDirectory(dir);
        var expectedPath = Path.Combine(dir, name + ".json");
        var actualPath = Path.Combine(dir, name + ".actual.json");

        if (!File.Exists(expectedPath))
        {
            File.WriteAllText(actualPath, actual);
            Assert.Fail($"Golden missing: {expectedPath}. Review {Path.GetFileName(actualPath)} and bless it (rename to {name}.json).");
        }

        var expected = File.ReadAllText(expectedPath).ReplaceLineEndings("\n");
        if (expected != actual)
        {
            File.WriteAllText(actualPath, actual);
            Assert.Fail($"Wire shape drift for '{name}'. Compare {name}.json vs {name}.actual.json; if intentional, bless the actual.");
        }

        if (File.Exists(actualPath))
            File.Delete(actualPath);
    }






    private static void Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToArray())
                {
                    var value = obj[key];
                    if (key == "created" && value is JsonValue cv && cv.TryGetValue<long>(out var unix) && unix > 0)
                    {
                        obj[key] = 0;
                        continue;
                    }
                    if (value is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        obj[key] = NormalizeString(s);
                        continue;
                    }
                    Normalize(value);
                }
                break;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        arr[i] = NormalizeString(s);
                        continue;
                    }
                    Normalize(arr[i]);
                }
                break;
        }
    }

    private static string NormalizeString(string s)
    {
        if (ResponseIdRegex().IsMatch(s)) return "<id>";
        if (QuoteIdRegex().IsMatch(s)) return "<quote_id>";
        if (TimestampRegex().IsMatch(s)) return "<timestamp>";
        if (SessionKeyRegex().IsMatch(s)) return "<session>";
        return s;
    }
}
