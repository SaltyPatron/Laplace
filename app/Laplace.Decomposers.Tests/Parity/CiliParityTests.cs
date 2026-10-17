using System.Text;
using System.Text.RegularExpressions;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.CILI;
using Laplace.Decomposers.Structured;
using Laplace.Decomposers.Tests.Structured;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Decomposers.Tests.Parity;

/// <summary>
/// Parity between the C# CILIDecomposer (the old path) and the CILI recipe
/// (recipes/cili/a895d7e) on the same sample of the real release, as #1885 step 1 and #1877
/// require before the C# decomposer is deleted. The two paths carry different source ids, so
/// entities are compared by id and claims by (subject, relation, object, context). Opt-in:
/// set LAPLACE_PARITY_CILI to the CILI release directory; the sample is the first
/// LAPLACE_PARITY_ROWS records (default 200) of each file both paths read.
/// </summary>
public sealed class CiliParityTests(ITestOutputHelper output)
{
    static CiliParityTests() => CodepointPerfcache.LoadDefault();

    private static readonly string[] SampledFiles =
        ["ili.ttl", "ili-map-pwn30.tab", "ili-map-pwn31.tab", "ili-map-odwn13.ttl",
         "older-wn-mappings/ili-map-pwn15.tab"];

    [Fact]
    public async Task Cili_recipe_and_csharp_decomposer_admit_the_same_entities_and_claims()
    {
        string? release = Environment.GetEnvironmentVariable("LAPLACE_PARITY_CILI");
        if (string.IsNullOrWhiteSpace(release) || !Directory.Exists(release)) return;
        int rows = int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_PARITY_ROWS"), out int r) ? r : 200;

        string sample = Path.Combine(Path.GetTempPath(), "cili-parity-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string rel in SampledFiles) WriteSample(release, sample, rel, rows);

            var old = await Drain(new CILIDecomposer(), sample);
            var recipe = await Drain(new Laplace.Decomposers.Structured.Decomposer<SourceGenerationRecipe>(
                SourceGenerationRecipe.Load(SourceGenerationRegressionTests.RepositoryFile("recipes/cili/a895d7e/cili.source.json")),
                sample), sample);

            var names = RelationNames();
            string report = Report(old, recipe, names);
            output.WriteLine(report);
            string? reportPath = Environment.GetEnvironmentVariable("LAPLACE_PARITY_REPORT");
            if (!string.IsNullOrWhiteSpace(reportPath)) File.WriteAllText(reportPath, report);

            Assert.True(old.Entities.SetEquals(recipe.Entities) && old.Claims.SetEquals(recipe.Claims),
                "CILI recipe is not at parity with CILIDecomposer:\n" + report);
        }
        finally { if (Directory.Exists(sample)) Directory.Delete(sample, true); }
    }

    private sealed record Admitted(HashSet<Hash128> Entities, HashSet<(Hash128 S, Hash128 T, Hash128? O, Hash128? C)> Claims);

    private static async Task<Admitted> Drain(IDecomposer decomposer, string root)
    {
        var entities = new HashSet<Hash128>();
        var claims = new HashSet<(Hash128, Hash128, Hash128?, Hash128?)>();
        var context = new FakeContext(new NullWriter()) { EcosystemPath = root };
        await decomposer.InitializeAsync(context);
        await foreach (var change in decomposer.DecomposeAsync(context, DecomposerOptions.Default).WithoutWriter())
        {
            foreach (var e in change.AllEntities()) entities.Add(e.Id);
            foreach (var a in change.AllAttestations()) claims.Add((a.SubjectId, a.TypeId, a.ObjectId, a.ContextId));
        }
        if (decomposer is IAsyncDisposable d) await d.DisposeAsync();
        return new Admitted(entities, claims);
    }

    private static string Report(Admitted old, Admitted recipe, IReadOnlyDictionary<Hash128, string> names)
    {
        string Name(Hash128 t) => names.TryGetValue(t, out var n) ? n : t.ToString();
        var sb = new StringBuilder();
        sb.AppendLine($"entities: csharp={old.Entities.Count} recipe={recipe.Entities.Count} " +
                      $"both={old.Entities.Intersect(recipe.Entities).Count()} " +
                      $"csharp_only={old.Entities.Except(recipe.Entities).Count()} recipe_only={recipe.Entities.Except(old.Entities).Count()}");
        sb.AppendLine($"claims:   csharp={old.Claims.Count} recipe={recipe.Claims.Count} " +
                      $"both={old.Claims.Intersect(recipe.Claims).Count()}");
        sb.AppendLine("claims by relation (csharp / recipe / both):");
        var oldPairs = old.Claims.Select(c => (c.S, c.O)).ToHashSet();
        sb.AppendLine($"  recipe claims whose (subject, object) the C# path also links, any relation: {recipe.Claims.Count(c => oldPairs.Contains((c.S, c.O)))}");
        var rels = old.Claims.Select(c => c.T).Concat(recipe.Claims.Select(c => c.T)).Distinct()
            .OrderBy(Name, StringComparer.Ordinal);
        foreach (var t in rels)
        {
            var a = old.Claims.Where(c => c.T == t).ToHashSet();
            var b = recipe.Claims.Where(c => c.T == t).ToHashSet();
            int sameTriple = a.Select(c => (c.S, c.O)).Intersect(b.Select(c => (c.S, c.O))).Count();
            sb.AppendLine($"  {Name(t),-28} {a.Count,8} {b.Count,8} {a.Intersect(b).Count(),8}  same subject/object ignoring context: {sameTriple}");
        }
        return sb.ToString();
    }

    private static IReadOnlyDictionary<Hash128, string> RelationNames()
    {
        string toml = File.ReadAllText(SourceGenerationRegressionTests.RepositoryFile("engine/manifest/relation_types.toml"));
        var map = new Dictionary<Hash128, string>();
        foreach (Match m in Regex.Matches(toml, "^canonical\\s*=\\s*\"(?<n>[^\"]+)\"", RegexOptions.Multiline))
        {
            string n = m.Groups["n"].Value;
            try { map.TryAdd(RelationTypeRegistry.RelationTypeId(n), n); } catch (Exception) { }
        }
        return map;
    }

    // The first `rows` records of a release file: tab files by line; Turtle by blank-line
    // separated blocks after the prefix header, so every sampled record is whole.
    private static void WriteSample(string release, string sample, string rel, int rows)
    {
        string src = Path.Combine(release, rel);
        if (!File.Exists(src)) return;
        string dst = Path.Combine(sample, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        using var reader = new StreamReader(src);
        using var writer = new StreamWriter(dst, false, new UTF8Encoding(false)) { NewLine = "\n" };
        bool turtle = rel.EndsWith(".ttl", StringComparison.Ordinal);
        int records = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (turtle)
            {
                bool header = line.StartsWith("@prefix", StringComparison.Ordinal) || line.StartsWith("PREFIX", StringComparison.Ordinal);
                if (!header && line.TrimEnd().EndsWith('.') && ++records >= rows) { writer.WriteLine(line); break; }
            }
            else if (line.Length > 0 && ++records > rows) break;
            writer.WriteLine(line);
        }
    }
}