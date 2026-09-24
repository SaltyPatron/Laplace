using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Structured;

public sealed record ResolvedSourceArtifact(
    IngestArtifact Artifact,
    SourceGenerationArtifactRule Rule,
    SemanticSourceRecipe? Recipe,
    string Provider,
    int RecordDepth,
    Hash128 ArtifactId,
    IReadOnlyList<Hash128> Dependencies);

public sealed record ResolvedSourceGeneration(
    SourceGenerationRecipe Recipe,
    string Root,
    IngestArtifactGraph Graph,
    Hash128 GenerationId,
    IReadOnlyList<ResolvedSourceArtifact> Bindings,
    IReadOnlyList<string> ExecutionErrors,
    bool CoversGeneration = true);

/// <summary>
/// Resolves the complete physical file set before execution, following the source
/// bundle's relative-path and exact-byte identity law. No source-specific admission
/// or persistence semantics live in this configuration boundary.
/// </summary>
public static class SourceGenerationResolver
{
    public static Task<ResolvedSourceGeneration> ResolveAsync(
        SourceGenerationRecipe recipe, string root, CancellationToken ct = default)
        => ResolveAsync(recipe, root, scope: null, ct);

    /// <summary>
    /// Resolves the generation at <paramref name="root"/>. A <paramref name="scope"/> at or
    /// beneath the root executes only the admitted artifacts under it plus their dependency
    /// closure; artifact paths, artifact ids and the generation id stay those of the whole
    /// generation, so a scoped run commits the same per-file facts a full run would.
    /// </summary>
    public static async Task<ResolvedSourceGeneration> ResolveAsync(
        SourceGenerationRecipe recipe, string root, string? scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        RejectLink(new DirectoryInfo(root));
        string? scopePath = null;
        if (!string.IsNullOrWhiteSpace(scope))
        {
            scopePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scope));
            if (!Contains(root, scopePath))
                throw new ArgumentException($"Scope '{scopePath}' is not within source-generation root '{root}'.", nameof(scope));
            if (!Directory.Exists(scopePath) && !File.Exists(scopePath))
                throw new FileNotFoundException("Source-generation scope does not exist.", scopePath);
            if (string.Equals(scopePath, Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal))
                scopePath = null;
        }
        var rules = DependencyOrder(recipe.Rules);
        var matchers = rules.ToDictionary(static rule => rule.Artifact.Selector,
            static rule => Selector(rule.Artifact.Selector), StringComparer.Ordinal);
        var semantic = new Dictionary<string, SemanticSourceRecipe>(StringComparer.Ordinal);
        var recipeErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();
            if (rule.RecipePath is not { } relative) continue;
            if (semantic.ContainsKey(relative) || recipeErrors.ContainsKey(relative)) continue;
            try
            {
                string path = ResolveBeneath(Path.GetDirectoryName(recipe.ManifestPath)!, relative);
                SemanticSourceRecipe selected = new LaplaceCookbook().InstallJson(path);
                if (selected.Authority != recipe.Authority || selected.Release != recipe.Release)
                    throw new InvalidDataException($"Recipe '{relative}' does not belong to {recipe.Authority}/{recipe.Release}.");
                semantic.Add(relative, selected);
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException
                || error is JsonException || error is ArgumentException || error is UnauthorizedAccessException)
            {
                recipeErrors.Add(relative, error.Message);
            }
        }

        var artifacts = new List<IngestArtifact>();
        var executionErrors = new List<string>();
        var matches = rules.ToDictionary(static rule => rule.Artifact.Selector,
            static _ => new List<IngestArtifact>(), StringComparer.Ordinal);
        foreach (string path in EnumerateFiles(root, ct).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            SourceGenerationArtifactRule? match = null;
            foreach (var rule in rules)
            {
                if (!matchers[rule.Artifact.Selector].IsMatch(relative)) continue;
                if (match is not null)
                    throw new InvalidDataException($"Artifact '{relative}' matches both '{match.Artifact.Selector}' and '{rule.Artifact.Selector}'.");
                match = rule;
            }
            if (match?.Artifact.Disposition == SourceArtifactDisposition.Absent)
                throw new InvalidDataException($"Artifact '{relative}' exists but its rule declares it absent.");
            // Hash the physical file once at the cold source boundary. The executor
            // verifies the same fingerprint while streaming its actual parse.
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long bytes = input.Length;
            string sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
            if (input.Length != bytes || input.Position != bytes)
                throw new IOException($"Artifact changed while fingerprinting: '{relative}'.");
            var artifact = MakeArtifact(recipe, relative, path,
                match is null ? IngestArtifactDisposition.Unsupported : Disposition(match.Artifact.Disposition),
                bytes, sha, match?.Artifact.Provider ?? "",
                match?.Artifact.Reason ?? (match is null ? "No source-generation rule accounts for this physical file." : ""),
                File.GetLastWriteTimeUtc(path));
            if (match?.RecipePath is { } selectedRecipe && recipeErrors.TryGetValue(selectedRecipe, out string? recipeError))
            {
                artifact = artifact with
                {
                    Disposition = IngestArtifactDisposition.Unsupported,
                    Notes = $"Semantic recipe '{selectedRecipe}' unavailable: {recipeError}",
                };
                executionErrors.Add($"Artifact '{relative}' cannot execute: {artifact.Notes}");
            }
            artifacts.Add(artifact);
            if (match is null) executionErrors.Add($"Physical artifact '{relative}' has no source-generation disposition.");
            if (match is not null)
            {
                matches[match.Artifact.Selector].Add(artifact);
            }
        }

        foreach (var rule in rules)
        {
            if (matches[rule.Artifact.Selector].Count != 0
                || (!rule.Artifact.Required && rule.Artifact.Disposition != SourceArtifactDisposition.Absent)) continue;
            artifacts.Add(MakeArtifact(recipe, rule.Artifact.Selector,
                Path.Combine(root, rule.Artifact.Selector), IngestArtifactDisposition.Absent,
                null, "", rule.Artifact.Provider,
                rule.Artifact.Reason ?? $"Required selector '{rule.Artifact.Selector}' matched no physical file.", null));
            if (rule.Artifact.Required) executionErrors.Add($"Required selector '{rule.Artifact.Selector}' matched no physical file.");
        }

        var bindings = new List<ResolvedSourceArtifact>();
        foreach (var rule in rules)
        {
            if (rule.Artifact.Disposition != SourceArtifactDisposition.Admitted) continue;
            var dependencyArtifacts = (rule.Artifact.DependsOn ?? [])
                .SelectMany(selector => matches[selector]).ToArray();
            string[] unavailable = (rule.Artifact.DependsOn ?? []).Where(selector =>
                matches[selector].Count == 0 || matches[selector].Any(static artifact =>
                    artifact.Disposition is not (IngestArtifactDisposition.Admitted or IngestArtifactDisposition.EquivalentPackaging)))
                .ToArray();
            foreach (var artifact in matches[rule.Artifact.Selector].ToArray())
            {
                if (!artifact.IsSelected) continue;
                if (unavailable.Length > 0)
                {
                    var unsupported = artifact with
                    {
                        Disposition = IngestArtifactDisposition.Unsupported,
                        Notes = $"Unresolved source-generation dependencies: {string.Join(", ", unavailable)}.",
                    };
                    artifacts[artifacts.IndexOf(artifact)] = unsupported;
                    // Propagate this disposition to later dependent rules.
                    int index = matches[rule.Artifact.Selector].IndexOf(artifact);
                    matches[rule.Artifact.Selector][index] = unsupported;
                    executionErrors.Add($"Artifact '{artifact.RelativePath}' cannot execute: {unsupported.Notes}");
                    continue;
                }
                bindings.Add(new(artifact, rule,
                    rule.RecipePath is { } recipePath ? semantic[recipePath] : null,
                    rule.Artifact.Provider, rule.RecordDepth, SourceArtifactProvenance.Resolve(artifact).ArtifactId,
                    dependencyArtifacts.Select(static item => SourceArtifactProvenance.Resolve(item).ArtifactId)
                        .Distinct().ToArray()));
            }
        }

        bool covers = scopePath is null;
        IReadOnlyList<ResolvedSourceArtifact> executed = bindings;
        if (scopePath is not null)
        {
            var byId = bindings.ToDictionary(static binding => binding.ArtifactId);
            var selected = new HashSet<Hash128>();
            var frontier = new Stack<ResolvedSourceArtifact>(
                bindings.Where(binding => Contains(scopePath, binding.Artifact.Path)));
            while (frontier.TryPop(out var binding))
            {
                if (!selected.Add(binding.ArtifactId)) continue;
                foreach (Hash128 dependency in binding.Dependencies)
                    if (byId.TryGetValue(dependency, out var required)) frontier.Push(required);
            }
            executed = bindings.Where(binding => selected.Contains(binding.ArtifactId)).ToArray();
            if (executed.Count == 0)
                executionErrors.Add($"Scope '{scopePath}' selects no admitted artifact of {recipe.SourceName}/{recipe.Release}.");
            covers = executed.Count == bindings.Count;
        }

        var graph = new IngestArtifactGraph(artifacts.OrderBy(static a => a.RelativePath, StringComparer.Ordinal));
        // A resolved generation is a composition: the generation document's content, then
        // the content ids of the semantic recipes it binds, then the admitted artifacts.
        var constituents = new List<Hash128>
        {
            ContentEmitter.RootId(recipe.CanonicalForm)
                ?? throw new InvalidOperationException(
                    $"source generation {recipe.SourceName}/{recipe.Release} could not be composed as content"),
        };
        foreach (var item in semantic.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            constituents.Add(item.Value.RecipeId);
        foreach (var binding in bindings.OrderBy(static b => b.Artifact.RelativePath, StringComparer.Ordinal))
            constituents.Add(binding.ArtifactId);
        Hash128 generation = Hash128.Merkle(EntityTier.Document, constituents.ToArray());
        return new(recipe, root, graph, generation, executed, executionErrors.AsReadOnly(), covers);
    }

    private static bool Contains(string directory, string path)
    {
        string parent = Path.TrimEndingDirectorySeparator(directory);
        return string.Equals(path, parent, StringComparison.Ordinal)
            || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static IngestArtifact MakeArtifact(SourceGenerationRecipe recipe, string relative, string path,
        IngestArtifactDisposition disposition, long? bytes, string sha, string provider, string notes,
        DateTimeOffset? modified) => new(recipe.SourceName, recipe.Release, relative, relative, path,
            disposition, "", "", bytes, sha, "", "", "", recipe.Authority, "", "", provider, notes,
            ModifiedAt: modified);

    private static IngestArtifactDisposition Disposition(SourceArtifactDisposition value) => value switch
    {
        SourceArtifactDisposition.Excluded => IngestArtifactDisposition.ExcludedWithReason,
        SourceArtifactDisposition.Admitted => IngestArtifactDisposition.Admitted,
        SourceArtifactDisposition.EquivalentPackaging => IngestArtifactDisposition.EquivalentPackaging,
        SourceArtifactDisposition.Superseded => IngestArtifactDisposition.Superseded,
        SourceArtifactDisposition.Unsupported => IngestArtifactDisposition.Unsupported,
        SourceArtifactDisposition.Absent => IngestArtifactDisposition.Absent,
        _ => throw new InvalidDataException("Unknown source artifact disposition."),
    };

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken ct)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new(root));
        while (pending.TryPop(out var directory))
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                RejectLink(entry);
                if (entry is DirectoryInfo child) pending.Push(child);
                else yield return entry.FullName;
            }
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Source bundle path contains a symbolic link: '{entry.FullName}'.");
    }

    private static string ResolveBeneath(string root, string relative)
    {
        string current = Path.GetFullPath(root);
        RejectLink(new DirectoryInfo(current));
        foreach (string segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!entry.Exists && entry.LinkTarget is null)
                throw new FileNotFoundException("Source generation references a missing recipe path.", current);
            RejectLink(entry);
        }
        return current;
    }

    private static Regex Selector(string glob)
    {
        var expression = new StringBuilder("\\A");
        for (int i = 0; i < glob.Length; ++i)
        {
            if (glob[i] == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    ++i;
                    if (i + 1 < glob.Length && glob[i + 1] == '/') { ++i; expression.Append("(?:.*/)?"); }
                    else expression.Append(".*");
                }
                else expression.Append("[^/]*");
            }
            else expression.Append(glob[i] == '?' ? "[^/]" : Regex.Escape(glob[i].ToString()));
        }
        expression.Append("\\z");
        return new(expression.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.Singleline);
    }

    private static IReadOnlyList<SourceGenerationArtifactRule> DependencyOrder(IReadOnlyList<SourceGenerationArtifactRule> rules)
    {
        var bySelector = rules.ToDictionary(static rule => rule.Artifact.Selector, StringComparer.Ordinal);
        var state = new Dictionary<string, byte>(StringComparer.Ordinal);
        var sorted = new List<SourceGenerationArtifactRule>();
        void Visit(SourceGenerationArtifactRule rule)
        {
            string selector = rule.Artifact.Selector;
            if (state.TryGetValue(selector, out byte status))
            {
                if (status == 1) throw new InvalidDataException($"Source-generation dependency cycle at '{selector}'.");
                return;
            }
            state[selector] = 1;
            foreach (string dependency in rule.Artifact.DependsOn ?? []) Visit(bySelector[dependency]);
            state[selector] = 2;
            sorted.Add(rule);
        }
        foreach (var rule in rules.OrderBy(static r => r.Artifact.Selector, StringComparer.Ordinal)) Visit(rule);
        return sorted;
    }

}
