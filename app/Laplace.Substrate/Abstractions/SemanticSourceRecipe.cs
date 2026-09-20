using System.Text;
using System.Globalization;
using System.Numerics;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

[Flags]
public enum SourceFieldDisposition
{
    None = 0,
    Identity = 1 << 0,
    Content = 1 << 1,
    Physicality = 1 << 2,
    Trajectory = 1 << 3,
    Occurrence = 1 << 4,
    Reference = 1 << 5,
    Testimony = 1 << 6,
    Provenance = 1 << 7,
    Calculation = 1 << 8,
    Packaging = 1 << 9,
    Excluded = 1 << 10,
}

public enum SourceValueKind
{
    Identity,
    Boolean,
    Enumerated,
    EnumeratedSequence,
    Integer,
    ExactNumber,
    Codepoint,
    CodepointSequence,
    Text,
    StructuredText,
}

public enum SourceReferenceCodec
{
    None,
    UnicodeCodepoint,
    UnicodeCodepointSequence,
    UPlusCodepointWithQualifier,
}

/// <summary>
/// Declarative lowering of one concrete syntax field into the universal substrate.
/// PropertyName is the canonical semantic identity after source alias resolution;
/// SyntaxPath is provider-owned concrete syntax.  One value may deliberately have
/// several dispositions (for example exact sentence content plus attributed testimony).
/// </summary>
public sealed record SourceRecipeField(
    string SyntaxPath,
    string PropertyName,
    SourceValueKind ValueKind,
    SourceFieldDisposition Disposition,
    string? AbsentSentinel = null,
    string? SequenceSeparator = null,
    string? RelationName = null,
    string? ObjectNamespace = null,
    string ObjectEntityType = "Recipe_Value",
    SourceReferenceCodec ReferenceCodec = SourceReferenceCodec.None,
    bool PreserveLexicalValue = false,
    string? RelationParent = null,
    double? RelationRank = null,
    string? LexicalRelationName = null,
    string? ValueAliasProperty = null,
    string? ContextField = null,
    string? DefaultValue = null,
    bool OmitDefaultTestimony = false);

public sealed record SourceRecipeStructure(
    string SyntaxPath,
    string SemanticType,
    SourceFieldDisposition Disposition);

public enum SourceSubjectBindingKind
{
    CodepointRange,
    ContentField,
    ClassifierField,
}

public sealed record SourceRecipeSubjectBinding(
    SourceSubjectBindingKind Kind,
    string IdentityField,
    string? RangeStartField = null,
    string? LastField = null,
    string? EntityNamespace = null,
    string EntityType = "Recipe_Subject",
    string? SequenceSeparator = null);

public sealed record SourceRecipeProviderRoute(
    string RecordName,
    string NamespaceUri,
    string FieldPrefix,
    SourceRecipeSubjectBinding Subject,
    IReadOnlyList<string> StructurePaths,
    IReadOnlyDictionary<string, string>? ChildFieldPrefixes = null,
    string? RangeRelationProperty = null,
    string? RangeRelationName = null,
    string? RangeStartField = null,
    string? RangeEndField = null);

public enum SourceArtifactDisposition
{
    Admitted,
    EquivalentPackaging,
    Superseded,
    Excluded,
    Unsupported,
    Absent,
}

/// <summary>One member of the complete physical artifact graph bound by a source generation.</summary>
public sealed record SourceRecipeArtifact(
    string Selector,
    string Provider,
    string Syntax,
    SourceArtifactDisposition Disposition,
    bool Required,
    string? Reason = null,
    IReadOnlyList<string>? DependsOn = null,
    IReadOnlyList<string>? ProviderRoutes = null);

/// <summary>Concrete delimited syntax; field meanings remain ordinary recipe rules.</summary>
public sealed record SourceDelimitedSyntax(
    string RecordName,
    IReadOnlyList<string> Columns,
    string Separator = ";",
    string CommentPrefix = "#",
    bool TrimFields = true,
    string NamespaceUri = "",
    string DirectivePrefix = "",
    string DirectiveRecordName = "",
    IReadOnlyList<string>? DirectiveColumns = null,
    string RangeColumn = "",
    string RangeSeparator = "..",
    string RangeFirstField = "first",
    string RangeLastField = "last",
    int MinimumColumns = 0,
    bool AllowTrailingEmptyColumn = false);

/// <summary>
/// Versioned, deterministic semantic recipe.  It is independent of batching,
/// concurrency and storage tuning: those change execution, not what an assertion means.
/// </summary>
public sealed class SemanticSourceRecipe
{
    private readonly IReadOnlyDictionary<string, SourceRecipeField> _fields;
    private readonly IReadOnlyList<SourceRecipeField> _fieldList;
    private readonly IReadOnlyDictionary<string, SourceRecipeStructure> _structures;
    private readonly IReadOnlyDictionary<string, string> _valueAliases;
    private readonly IReadOnlyDictionary<string, SourceRecipeProviderRoute> _providerRoutes;
    private readonly IReadOnlyDictionary<string, string> _propertyAliases;
    private readonly IReadOnlyDictionary<string, SourceRecipeArtifact> _artifacts;

    public SemanticSourceRecipe(
        string authority,
        string release,
        string provider,
        string syntax,
        IEnumerable<SourceRecipeField> fields,
        IEnumerable<SourceRecipeStructure>? structures = null,
        IReadOnlyDictionary<string, string>? valueAliases = null,
        IEnumerable<SourceRecipeProviderRoute>? providerRoutes = null,
        IEnumerable<SourceRecipeArtifact>? artifacts = null,
        SourceDelimitedSyntax? delimitedSyntax = null)
    {
        Authority = Required(authority, nameof(authority));
        Release = Required(release, nameof(release));
        Provider = Required(provider, nameof(provider));
        Syntax = Required(syntax, nameof(syntax));
        DelimitedSyntax = delimitedSyntax;
        if (delimitedSyntax is { } delimited)
        {
            Required(delimited.RecordName, nameof(delimitedSyntax));
            Required(delimited.Separator, nameof(delimitedSyntax));
            if (delimited.Columns.Count == 0 || delimited.Columns.Any(string.IsNullOrWhiteSpace)
                || delimited.Columns.Distinct(StringComparer.Ordinal).Count() != delimited.Columns.Count
                || delimited.Separator.IndexOfAny(['\r', '\n']) >= 0
                || delimited.MinimumColumns < 0 || delimited.MinimumColumns > delimited.Columns.Count)
                throw new ArgumentException("Delimited syntax requires distinct columns and a non-line separator.", nameof(delimitedSyntax));
            if (delimited.DirectivePrefix.Length != 0 && (delimited.DirectiveRecordName.Length == 0
                || delimited.DirectiveColumns is not { Count: > 0 }))
                throw new ArgumentException("Directive syntax requires a record route and columns.", nameof(delimitedSyntax));
            if (delimited.RangeColumn.Length != 0 && (delimited.RangeSeparator.Length == 0
                || delimited.RangeFirstField.Length == 0 || delimited.RangeLastField.Length == 0))
                throw new ArgumentException("Range syntax requires separator and endpoint fields.", nameof(delimitedSyntax));
        }

        SourceRecipeField[] fieldArray = fields?.ToArray()
            ?? throw new ArgumentNullException(nameof(fields));
        if (fieldArray.Length == 0)
            throw new ArgumentException("A semantic recipe must account for at least one field.", nameof(fields));

        var map = new Dictionary<string, SourceRecipeField>(StringComparer.Ordinal);
        foreach (SourceRecipeField field in fieldArray)
        {
            Validate(field);
            if (!map.TryAdd(field.SyntaxPath, field))
                throw new ArgumentException($"Duplicate recipe syntax path '{field.SyntaxPath}'.", nameof(fields));
        }
        _fields = map;
        _fieldList = fieldArray.OrderBy(static f => f.SyntaxPath, StringComparer.Ordinal).ToArray();
        var propertyAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SourceRecipeField field in fieldArray)
        {
            int attribute = field.SyntaxPath.LastIndexOf("/@", StringComparison.Ordinal);
            if (attribute < 0) continue;
            string alias = field.SyntaxPath[(attribute + 2)..];
            if (propertyAliases.TryGetValue(alias, out string? existing)
                && existing != field.PropertyName)
                continue;
            propertyAliases[alias] = field.PropertyName;
        }
        _propertyAliases = propertyAliases;

        Structures = (structures ?? []).OrderBy(static s => s.SyntaxPath, StringComparer.Ordinal).ToArray();
        var structureMap = new Dictionary<string, SourceRecipeStructure>(StringComparer.Ordinal);
        foreach (SourceRecipeStructure structure in Structures)
        {
            Required(structure.SyntaxPath, nameof(structures));
            Required(structure.SemanticType, nameof(structures));
            ValidateDisposition(structure.Disposition, structure.SyntaxPath);
            if (!structureMap.TryAdd(structure.SyntaxPath, structure))
                throw new ArgumentException(
                    $"Duplicate recipe structure path '{structure.SyntaxPath}'.", nameof(structures));
        }
        _structures = structureMap;

        _valueAliases = valueAliases is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(valueAliases, StringComparer.Ordinal);

        ProviderRoutes = (providerRoutes ?? []).OrderBy(
            static route => route.RecordName, StringComparer.Ordinal).ToArray();
        _providerRoutes = ProviderRoutes.ToDictionary(
            static route => route.RecordName, StringComparer.Ordinal);

        Artifacts = (artifacts ?? []).OrderBy(
            static artifact => artifact.Selector, StringComparer.Ordinal).ToArray();
        _artifacts = Artifacts.ToDictionary(
            static artifact => artifact.Selector, StringComparer.Ordinal);
        foreach (SourceRecipeArtifact artifact in Artifacts)
        {
            Required(artifact.Selector, nameof(artifacts));
            Required(artifact.Provider, nameof(artifacts));
            Required(artifact.Syntax, nameof(artifacts));
            if (artifact.Disposition is SourceArtifactDisposition.Excluded
                or SourceArtifactDisposition.Unsupported
                or SourceArtifactDisposition.Absent
                && string.IsNullOrWhiteSpace(artifact.Reason))
                throw new ArgumentException(
                    $"Artifact '{artifact.Selector}' requires an explicit disposition reason.",
                    nameof(artifacts));
        }

        CanonicalForm = BuildCanonicalForm(
            fieldArray, Structures, _valueAliases, ProviderRoutes, Artifacts);
        RecipeId = Hash128.OfCanonical(CanonicalForm);
    }

    public string Authority { get; }
    public string Release { get; }
    public string Provider { get; }
    public string Syntax { get; }
    public SourceDelimitedSyntax? DelimitedSyntax { get; }
    public IReadOnlyList<SourceRecipeField> Fields => _fieldList;
    public IReadOnlyList<SourceRecipeStructure> Structures { get; }
    public IReadOnlyDictionary<string, string> ValueAliases => _valueAliases;
    public IReadOnlyList<SourceRecipeProviderRoute> ProviderRoutes { get; }
    public IReadOnlyList<SourceRecipeArtifact> Artifacts { get; }
    public string CanonicalForm { get; }
    public Hash128 RecipeId { get; }

    public SourceRecipeField Field(string syntaxPath) =>
        _fields.TryGetValue(syntaxPath, out SourceRecipeField? field)
            ? field
            : throw new KeyNotFoundException(
                $"Recipe {Authority}/{Release} has no disposition for syntax field '{syntaxPath}'.");

    public bool TryField(string syntaxPath, out SourceRecipeField field) =>
        _fields.TryGetValue(syntaxPath, out field!);

    public SourceRecipeStructure Structure(string syntaxPath) =>
        _structures.TryGetValue(syntaxPath, out SourceRecipeStructure? structure)
            ? structure
            : throw new KeyNotFoundException(
                $"Recipe {Authority}/{Release} has no disposition for syntax structure '{syntaxPath}'.");

    public bool TryStructure(string syntaxPath, out SourceRecipeStructure structure) =>
        _structures.TryGetValue(syntaxPath, out structure!);

    public string CanonicalValue(string propertyName, string value)
    {
        string aliasProperty = Fields.FirstOrDefault(field =>
            field.PropertyName == propertyName && field.ValueAliasProperty is not null)
            ?.ValueAliasProperty ?? propertyName;
        return _valueAliases.TryGetValue(ValueAliasKey(aliasProperty, value), out string? canonical)
            ? canonical : value;
    }

    public string CanonicalProperty(string alias) =>
        _propertyAliases.TryGetValue(alias, out string? canonical) ? canonical : alias;

    public SourceRecipeProviderRoute ProviderRoute(string recordName) =>
        _providerRoutes.TryGetValue(recordName, out SourceRecipeProviderRoute? route)
            ? route
            : throw new KeyNotFoundException(
                $"Recipe {Authority}/{Release} has no provider route for record '{recordName}'.");

    public SourceRecipeArtifact Artifact(string selector) =>
        _artifacts.TryGetValue(selector, out SourceRecipeArtifact? artifact)
            ? artifact
            : throw new KeyNotFoundException(
                $"Recipe {Authority}/{Release} has no artifact disposition for '{selector}'.");

    private string BuildCanonicalForm(
        IEnumerable<SourceRecipeField> fields,
        IEnumerable<SourceRecipeStructure> structures,
        IReadOnlyDictionary<string, string> valueAliases,
        IEnumerable<SourceRecipeProviderRoute> providerRoutes,
        IEnumerable<SourceRecipeArtifact> artifacts)
    {
        var canonical = new StringBuilder("laplace/source-recipe/v1");
        Append(canonical, Authority);
        Append(canonical, Release);
        Append(canonical, Provider);
        Append(canonical, Syntax);
        if (DelimitedSyntax is { } delimited)
        {
            canonical.Append("|delimited/v1");
            Append(canonical, delimited.RecordName);
            Append(canonical, delimited.NamespaceUri);
            Append(canonical, delimited.Separator);
            Append(canonical, delimited.CommentPrefix);
            Append(canonical, delimited.TrimFields ? "1" : "0");
            Append(canonical, delimited.DirectivePrefix);
            Append(canonical, delimited.DirectiveRecordName);
            Append(canonical, delimited.Columns.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string column in delimited.Columns) Append(canonical, column);
            Append(canonical, (delimited.DirectiveColumns?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
            foreach (string column in delimited.DirectiveColumns ?? []) Append(canonical, column);
            Append(canonical, delimited.RangeColumn);
            Append(canonical, delimited.RangeSeparator);
            Append(canonical, delimited.RangeFirstField);
            Append(canonical, delimited.RangeLastField);
            Append(canonical, delimited.MinimumColumns.ToString(CultureInfo.InvariantCulture));
            Append(canonical, delimited.AllowTrailingEmptyColumn ? "1" : "0");
        }
        foreach (SourceRecipeField field in fields.OrderBy(static f => f.SyntaxPath, StringComparer.Ordinal))
        {
            canonical.Append("|f");
            Append(canonical, field.SyntaxPath);
            Append(canonical, field.PropertyName);
            Append(canonical, field.ValueKind.ToString());
            Append(canonical, ((int)field.Disposition).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(canonical, field.AbsentSentinel ?? "");
            Append(canonical, field.SequenceSeparator ?? "");
            Append(canonical, field.RelationName ?? "");
            Append(canonical, field.ObjectNamespace ?? "");
            Append(canonical, field.ObjectEntityType);
            Append(canonical, field.ReferenceCodec.ToString());
            Append(canonical, field.PreserveLexicalValue ? "1" : "0");
            Append(canonical, field.RelationParent ?? "");
            Append(canonical, field.RelationRank?.ToString("R", CultureInfo.InvariantCulture) ?? "");
            Append(canonical, field.LexicalRelationName ?? "");
            Append(canonical, field.ValueAliasProperty ?? "");
            if (field.ContextField is not null)
            {
                canonical.Append("|context");
                Append(canonical, field.ContextField);
            }
            if (field.DefaultValue is not null || field.OmitDefaultTestimony)
            {
                canonical.Append("|default");
                Append(canonical, field.DefaultValue ?? "");
                Append(canonical, field.OmitDefaultTestimony ? "1" : "0");
            }
        }
        foreach (SourceRecipeStructure structure in structures)
        {
            canonical.Append("|s");
            Append(canonical, structure.SyntaxPath);
            Append(canonical, structure.SemanticType);
            Append(canonical, ((int)structure.Disposition).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        foreach ((string key, string value) in valueAliases.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            canonical.Append("|a");
            Append(canonical, key);
            Append(canonical, value);
        }
        foreach (SourceRecipeProviderRoute route in providerRoutes)
        {
            canonical.Append("|r");
            Append(canonical, route.RecordName);
            Append(canonical, route.NamespaceUri);
            Append(canonical, route.FieldPrefix);
            Append(canonical, route.Subject.Kind.ToString());
            Append(canonical, route.Subject.IdentityField);
            Append(canonical, route.Subject.RangeStartField ?? "");
            Append(canonical, route.Subject.LastField ?? "");
            Append(canonical, route.Subject.EntityNamespace ?? "");
            Append(canonical, route.Subject.EntityType);
            Append(canonical, route.Subject.SequenceSeparator ?? "");
            Append(canonical, route.RangeRelationProperty ?? "");
            Append(canonical, route.RangeRelationName ?? "");
            Append(canonical, route.RangeStartField ?? "");
            Append(canonical, route.RangeEndField ?? "");
            foreach (string path in route.StructurePaths.Order(StringComparer.Ordinal)) Append(canonical, path);
            foreach ((string child, string prefix) in (route.ChildFieldPrefixes
                         ?? new Dictionary<string, string>()).OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                Append(canonical, child);
                Append(canonical, prefix);
            }
        }
        foreach (SourceRecipeArtifact artifact in artifacts)
        {
            canonical.Append("|g");
            Append(canonical, artifact.Selector);
            Append(canonical, artifact.Provider);
            Append(canonical, artifact.Syntax);
            Append(canonical, artifact.Disposition.ToString());
            Append(canonical, artifact.Required ? "1" : "0");
            Append(canonical, artifact.Reason ?? "");
            foreach (string dependency in (artifact.DependsOn ?? []).Order(StringComparer.Ordinal))
                Append(canonical, dependency);
            foreach (string route in (artifact.ProviderRoutes ?? []).Order(StringComparer.Ordinal))
                Append(canonical, route);
        }
        return canonical.ToString();
    }

    private static void Append(StringBuilder target, string value) =>
        target.Append('|').Append(value.Length).Append(':').Append(value);

    private static string Required(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Recipe values cannot be empty.", parameter);
        return value;
    }

    private static void Validate(SourceRecipeField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        Required(field.SyntaxPath, nameof(field.SyntaxPath));
        Required(field.PropertyName, nameof(field.PropertyName));
        ValidateDisposition(field.Disposition, field.SyntaxPath);
        if (field.Disposition.HasFlag(SourceFieldDisposition.Excluded)
            && field.Disposition != SourceFieldDisposition.Excluded)
            throw new ArgumentException(
                $"Excluded field '{field.SyntaxPath}' cannot also be admitted.", nameof(field));
        if (field.OmitDefaultTestimony && field.DefaultValue is null)
            throw new ArgumentException(
                $"Field '{field.SyntaxPath}' cannot omit default testimony without declaring its semantic default.",
                nameof(field));
        if (field.OmitDefaultTestimony
            && !field.Disposition.HasFlag(SourceFieldDisposition.Testimony))
            throw new ArgumentException(
                $"Field '{field.SyntaxPath}' cannot omit default testimony when it is not testimony.",
                nameof(field));
        if (field.DefaultValue is not null && field.AbsentSentinel is not null
            && string.Equals(field.DefaultValue, field.AbsentSentinel, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Field '{field.SyntaxPath}' cannot use the same lexical value for absence and semantic default.",
                nameof(field));
    }

    private static void ValidateDisposition(SourceFieldDisposition disposition, string syntaxPath)
    {
        if (disposition == SourceFieldDisposition.None)
            throw new ArgumentException($"Field or structure '{syntaxPath}' has no disposition.");
    }

    public static string ValueAliasKey(string propertyName, string value)
    {
        var normalized = new StringBuilder(value.Length);
        foreach (char c in value)
            if (c is not ('_' or '-' or ' ' or '\t'))
                normalized.Append(char.ToUpperInvariant(c));
        return $"{propertyName}\0{normalized}";
    }
}

public readonly record struct LaplaceRecipeKey(
    string Authority,
    string Release,
    string Provider,
    string Syntax);

public enum LaplaceRecipeFieldChangeKind
{
    Added,
    Removed,
    Changed,
}

public sealed record LaplaceRecipeFieldChange(
    string SyntaxPath,
    LaplaceRecipeFieldChangeKind Kind,
    SourceRecipeField? Before,
    SourceRecipeField? After);

public sealed record LaplaceRecipeSchemaReport(
    IReadOnlyList<string> UnknownProviderFields,
    IReadOnlyList<string> MissingRecipeFields)
{
    public bool IsComplete => UnknownProviderFields.Count == 0 && MissingRecipeFields.Count == 0;
}

/// <summary>
/// Process-wide catalog of immutable Laplace Recipes. Providers register one
/// authority/release/provider/syntax generation; execution resolves the exact generation rather
/// than switching on a decomposer class name.
/// </summary>
public sealed class LaplaceCookbook
{
    public static LaplaceCookbook Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<LaplaceRecipeKey, SemanticSourceRecipe> _recipes = [];
    private readonly Dictionary<Hash128, SemanticSourceRecipe> _byId = [];

    public void Register(SemanticSourceRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var key = new LaplaceRecipeKey(
            recipe.Authority, recipe.Release, recipe.Provider, recipe.Syntax);
        lock (_gate)
        {
            if (_recipes.TryGetValue(key, out SemanticSourceRecipe? existing))
            {
                if (existing.RecipeId == recipe.RecipeId) return;
                throw new InvalidOperationException(
                    $"Recipe generation {key.Authority}/{key.Release}/{key.Provider}/{key.Syntax} "
                    + $"is already registered as {existing.RecipeId}; refusing {recipe.RecipeId}.");
            }
            if (_byId.TryGetValue(recipe.RecipeId, out SemanticSourceRecipe? collision))
                throw new InvalidOperationException(
                    $"Recipe id {recipe.RecipeId} is already registered for "
                    + $"{collision.Authority}/{collision.Release}/{collision.Syntax}.");
            _recipes.Add(key, recipe);
            _byId.Add(recipe.RecipeId, recipe);
        }
    }

    public SemanticSourceRecipe Resolve(LaplaceRecipeKey key)
    {
        lock (_gate)
            return _recipes.TryGetValue(key, out SemanticSourceRecipe? recipe)
                ? recipe
                : throw new KeyNotFoundException(
                    $"No Laplace Recipe is registered for "
                    + $"{key.Authority}/{key.Release}/{key.Provider}/{key.Syntax}.");
    }

    public SemanticSourceRecipe Resolve(Hash128 recipeId)
    {
        lock (_gate)
            return _byId.TryGetValue(recipeId, out SemanticSourceRecipe? recipe)
                ? recipe
                : throw new KeyNotFoundException($"No Laplace Recipe is registered as {recipeId}.");
    }

    public IReadOnlyList<SemanticSourceRecipe> Recipes
    {
        get
        {
            lock (_gate)
                return _recipes.Values
                    .OrderBy(static recipe => recipe.Authority, StringComparer.Ordinal)
                    .ThenBy(static recipe => recipe.Release, StringComparer.Ordinal)
                    .ThenBy(static recipe => recipe.Provider, StringComparer.Ordinal)
                    .ThenBy(static recipe => recipe.Syntax, StringComparer.Ordinal)
                    .ToArray();
        }
    }

    public static LaplaceRecipeSchemaReport ValidateProviderSchema(
        SemanticSourceRecipe recipe,
        IEnumerable<string> providerSyntaxPaths)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(providerSyntaxPaths);
        var provider = providerSyntaxPaths.ToHashSet(StringComparer.Ordinal);
        string[] unknown = provider
            .Where(path => !recipe.TryField(path, out _))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] missing = recipe.Fields
            .Select(static field => field.SyntaxPath)
            .Where(path => !provider.Contains(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new LaplaceRecipeSchemaReport(unknown, missing);
    }

    public static IReadOnlyList<LaplaceRecipeFieldChange> Diff(
        SemanticSourceRecipe before,
        SemanticSourceRecipe after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var paths = before.Fields.Select(static field => field.SyntaxPath)
            .Concat(after.Fields.Select(static field => field.SyntaxPath))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var changes = new List<LaplaceRecipeFieldChange>();
        foreach (string path in paths)
        {
            bool had = before.TryField(path, out SourceRecipeField oldField);
            bool has = after.TryField(path, out SourceRecipeField newField);
            if (!had)
                changes.Add(new LaplaceRecipeFieldChange(
                    path, LaplaceRecipeFieldChangeKind.Added, null, newField));
            else if (!has)
                changes.Add(new LaplaceRecipeFieldChange(
                    path, LaplaceRecipeFieldChangeKind.Removed, oldField, null));
            else if (oldField != newField)
                changes.Add(new LaplaceRecipeFieldChange(
                    path, LaplaceRecipeFieldChangeKind.Changed, oldField, newField));
        }
        return changes;
    }
}

public readonly record struct SourceRecipeAssertion<TSubject>(
    TSubject Subject,
    string SyntaxPath,
    string RawValue);

public readonly record struct SourceRecipeValue(
    string Raw,
    bool IsAbsent,
    bool? Boolean,
    BigInteger? Integer,
    IReadOnlyList<string> Sequence,
    bool IsDefault);

public interface ILaplaceRecipeLoweringTarget<TSubject>
{
    void Lower(
        in SourceRecipeAssertion<TSubject> assertion,
        SourceRecipeField field,
        in SourceRecipeValue value);
}

/// <summary>
/// Shared fail-closed recipe interpreter. Syntax providers emit assertions; this
/// interpreter resolves the selected recipe, preserves the exact lexical value, and
/// supplies typed boolean/integer/sequence views to a substrate lowering target.
/// </summary>
public sealed class LaplaceRecipeInterpreter<TSubject>
{
    public LaplaceRecipeInterpreter(SemanticSourceRecipe recipe) =>
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));

    public SemanticSourceRecipe Recipe { get; }

    public void Lower(
        in SourceRecipeAssertion<TSubject> assertion,
        ILaplaceRecipeLoweringTarget<TSubject> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        SourceRecipeField field = Recipe.Field(assertion.SyntaxPath);
        if (field.Disposition == SourceFieldDisposition.Excluded) return;
        SourceRecipeValue value = Parse(field, assertion.RawValue);
        target.Lower(assertion, field, value);
    }

    public void LowerBatch(
        IEnumerable<SourceRecipeAssertion<TSubject>> assertions,
        ILaplaceRecipeLoweringTarget<TSubject> target)
    {
        ArgumentNullException.ThrowIfNull(assertions);
        foreach (SourceRecipeAssertion<TSubject> assertion in assertions)
            Lower(assertion, target);
    }

    private static SourceRecipeValue Parse(SourceRecipeField field, string raw)
    {
        raw ??= string.Empty;
        bool absent = field.AbsentSentinel is not null
            && string.Equals(raw, field.AbsentSentinel, StringComparison.Ordinal);
        bool isDefault = field.DefaultValue is not null
            && string.Equals(raw, field.DefaultValue, StringComparison.Ordinal);
        bool? boolean = null;
        BigInteger? integer = null;
        IReadOnlyList<string> sequence = [];
        if (!absent)
        {
            if (field.ValueKind == SourceValueKind.Boolean)
                boolean = raw switch
                {
                    "Y" or "Yes" or "true" or "True" or "1" => true,
                    "N" or "No" or "false" or "False" or "0" => false,
                    _ => throw new InvalidDataException(
                        $"Recipe field '{field.SyntaxPath}' received invalid boolean '{raw}'."),
                };
            if (field.ValueKind == SourceValueKind.Integer)
            {
                if (!BigInteger.TryParse(
                        raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                        out BigInteger parsed))
                    throw new InvalidDataException(
                        $"Recipe field '{field.SyntaxPath}' received invalid integer '{raw}'.");
                integer = parsed;
            }
            if (field.ValueKind is SourceValueKind.CodepointSequence
                or SourceValueKind.EnumeratedSequence)
            {
                string separator = field.SequenceSeparator
                    ?? throw new InvalidDataException(
                        $"Recipe sequence field '{field.SyntaxPath}' has no separator.");
                sequence = raw.Split(
                    separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }
        return new SourceRecipeValue(raw, absent, boolean, integer, sequence, isDefault);
    }
}
