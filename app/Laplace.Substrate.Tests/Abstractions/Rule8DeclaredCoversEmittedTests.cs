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
/// Rule #8: a decomposer declares every relation it emits. Nothing enforced it.
///
/// DecomposerArchitectureGateTests has a case that LOOKS like it does --
/// FamilyAwareBootstrap_ChildPullsParentRoot -- but it asserts DeclaredCoversEmitted over
/// three hand-written literals (HAS_XPOS / HAS_POS / IS_A). It tests the helper. It walks no
/// source, reads no roster, and would pass unchanged if every decomposer in the repo
/// declared nothing at all.
///
/// A first replacement compared rosters against DISTINCT (source_id, type_id) in
/// laplace.attestations. That was worse: the db-tier fixture builds its OWN database holding
/// only SubstrateCanonical rows, so it matched zero sources and passed vacuously even with
/// HAS_BLOCK deleted from Unicode's roster -- the exact failure it was written to replace.
///
/// TWO THINGS THIS GETS RIGHT.
///
/// The roster is EVALUATED, never scraped. WordNetSource composes Relations from
/// DeclaredRelations plus the manifest's language scope plus its pointer families, so a
/// regex over `Relations { get; } = [...]` reports 1 where the answer is 33 -- which is how
/// a false count reached engine/manifest/relation_types.toml.
///
/// Coverage goes through SourceVocabularyBootstrap.DeclaredCoversEmitted, so declaring a
/// family root covers its children exactly as the ingest does. Reimplementing that
/// comparison here would be a second definition free to drift from the one that runs.
///
/// Emission is read from the decomposer's own source as governed relation-name literals.
/// That is a FLOOR, not a census: a relation reached only through a computed name is not
/// visible here. It is enough to fail when a declaration is deleted while its emit remains,
/// which is the regression this guards, and it needs no database.
/// </summary>
public sealed class Rule8DeclaredCoversEmittedTests
{
    private readonly ITestOutputHelper _out;
    public Rule8DeclaredCoversEmittedTests(ITestOutputHelper o) => _out = o;

    // Emitted for every source by SourceVocabularyBootstrap / BootstrapIntentBuilder, not by
    // any decomposer body, so a source cannot be asked to declare them.
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

        // A LANE, not a source. app/Laplace.Decomposers/SemLink defines SemLinkSource,
        // MapNetDecomposer, WordFrameNetDecomposer and PredicateMatrix together, and
        // SemLinkSources.cs alone holds two Relations rosters. Attributing the whole
        // directory's literals to one source reported seven false violations against
        // SemLink that are declared by a sibling in the same file. A literal in the lane is
        // emitted by one of the lane's sources, so the lane's UNION is what must cover it.
        var lanes = new Dictionary<string, (HashSet<string> Roster, List<string> Names)>(StringComparer.Ordinal);
        foreach (var (name, roster, type) in Sources())
        {
            // By NAMESPACE, not by source name. MapNetDecomposer, WordFrameNetDecomposer and
            // PredicateMatrix all live in the SemLink lane; mapping "MapNetDecomposer" to a
            // MapNet directory finds nothing, drops those rosters, and then reports the lane's
            // literals as undeclared by SemLink -- seven false violations.
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

        // Matching nothing is a failure, not a pass. That is how the previous attempt at this
        // gate reported success while verifying nothing.
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
