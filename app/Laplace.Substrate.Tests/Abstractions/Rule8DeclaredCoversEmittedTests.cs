using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Laplace.Decomposers.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Every source declares each relation its provider code names.
///
/// Rosters are evaluated, not scraped: a source's Relations can be composed at runtime
/// (WordNetSource adds manifest language scope and pointer families to its declared list).
/// Coverage uses SourceVocabularyBootstrap.DeclaredCoversEmitted, the same check ingest
/// runs, so a declared family root covers its children.
///
/// Emission is read from each provider directory as governed relation-name literals. That
/// is a lower bound: a relation reached only through a computed name is not seen. It fails
/// when a declaration is removed while its literal remains, and needs no database.
/// </summary>
public sealed class Rule8DeclaredCoversEmittedTests
{
    private readonly ITestOutputHelper _out;
    public Rule8DeclaredCoversEmittedTests(ITestOutputHelper o) => _out = o;

    // Attested for every source by SourceVocabularyBootstrap / BootstrapIntentBuilder, not by
    // provider code, so no source declares them.
    private static readonly HashSet<string> SpineProvenance = new(StringComparer.Ordinal)
    {
        "HAS_ATTRIBUTION", "HAS_CITATION", "HAS_LICENSE", "HAS_SOURCE_URL", "HAS_TRUST_CLASS",
    };

    private static string RepoRoot =>
        typeof(Rule8DeclaredCoversEmittedTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "LaplaceRepoRoot").Value!;

    private static HashSet<string> GovernedNames()
    {
        string toml = File.ReadAllText(Path.Combine(RepoRoot, "engine", "manifest", "relation_types.toml"));
        return new HashSet<string>(
            Regex.Matches(toml, @"^(?:canonical|surface)\s*=\s*""([A-Z][A-Z0-9_]*)""\s*$",
                          RegexOptions.Multiline).Select(m => m.Groups[1].Value),
            StringComparer.Ordinal);
    }

    /// Concrete ISeedSource implementors, rosters evaluated. ISeedSource declares these as
    /// static ABSTRACT members and invoking one through the interface throws
    /// BadImageFormatException, so interfaces and abstracts are excluded.
    private static IEnumerable<(string Name, IReadOnlyList<string> Relations, Type Type)> Sources()
    {
        _ = typeof(Laplace.Decomposers.Unicode.UnicodeSource).Assembly;   // force the lazy load
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(a => a.GetName().Name?.StartsWith("Laplace.", StringComparison.Ordinal) == true))
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
            foreach (var t in types)
            {
                if (t.IsInterface || t.IsAbstract || t.ContainsGenericParameters) continue;
                var rel = t.GetProperty("Relations", BindingFlags.Public | BindingFlags.Static);
                var nam = t.GetProperty("SourceName", BindingFlags.Public | BindingFlags.Static);
                if (rel is null || nam is null) continue;
                string? name; IReadOnlyList<string>? rels;
                try { name = nam.GetValue(null) as string; rels = rel.GetValue(null) as IReadOnlyList<string>; }
                catch (TargetInvocationException) { continue; }
                if (string.IsNullOrEmpty(name) || rels is null) continue;
                yield return (name, rels, t);
            }
        }
    }

    [Fact]
    public void EveryDecomposerDeclaresTheRelationsItsSourceEmits()
    {
        var governed = GovernedNames();
        Assert.NotEmpty(governed);

        var faults = new List<string>();

        // Rosters are grouped by provider directory: one directory can define several
        // sources (SemLink holds SemLinkSource, MapNet, WordFrameNet and PredicateMatrix),
        // and a literal there is covered by the union of its sources' rosters.
        var lanes = new Dictionary<string, (HashSet<string> Roster, List<string> Names)>(StringComparer.Ordinal);
        foreach (var (name, roster, type) in Sources())
        {
            // The directory comes from the type's namespace, not the source name.
            string? ns = type.Namespace;
            if (ns is null || !ns.StartsWith("Laplace.Decomposers.", StringComparison.Ordinal)) continue;
            string lane = Path.Combine(RepoRoot, "app", "Laplace.Decomposers",
                                       ns["Laplace.Decomposers.".Length..].Replace('.', Path.DirectorySeparatorChar));
            if (!Directory.Exists(lane)) continue;
            if (!lanes.TryGetValue(lane, out var e))
                lanes[lane] = e = (new HashSet<string>(StringComparer.Ordinal), new List<string>());
            foreach (string r in roster) e.Roster.Add(r);
            e.Names.Add(name);
        }

        foreach (var (lane, entry) in lanes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string cs in Directory.EnumerateFiles(lane, "*.cs", SearchOption.AllDirectories))
            {
                string body = File.ReadAllText(cs);
                foreach (Match m in Regex.Matches(body, "\"([A-Z][A-Z0-9_]*)\""))
                {
                    string lit = m.Groups[1].Value;
                    if (governed.Contains(lit) && !SpineProvenance.Contains(lit)) emitted.Add(lit);
                }
            }

            var missing = emitted
                .Where(r => !SourceVocabularyBootstrap.DeclaredCoversEmitted(entry.Roster, r))
                .OrderBy(r => r, StringComparer.Ordinal).ToList();

            string label = string.Join("+", entry.Names.OrderBy(n => n, StringComparer.Ordinal));
            _out.WriteLine($"{Path.GetFileName(lane),-16} {label}: declared={entry.Roster.Count} "
                           + $"literals={emitted.Count} undeclared={missing.Count}");
            if (missing.Count > 0)
                faults.Add($"{label} names but does not declare: {string.Join(", ", missing)}");
        }

        // Matching too few directories fails: a vacuous pass verifies nothing.
        Assert.True(lanes.Count >= 10,
            $"only {lanes.Count} decomposer lanes matched a declared roster — the gate "
            + "verified almost nothing, which is the failure mode it exists to replace");
        Assert.True(faults.Count == 0, string.Join("\n", faults));
    }

    private static string ShortName(string sourceName) =>
        sourceName.EndsWith("Decomposer", StringComparison.Ordinal)
            ? sourceName[..^"Decomposer".Length]
            : sourceName;
}
