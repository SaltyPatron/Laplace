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
    string? SequenceSeparator = null);

public sealed record SourceRecipeStructure(
    string SyntaxPath,
    string SemanticType,
    SourceFieldDisposition Disposition);

/// <summary>
/// Versioned, deterministic semantic recipe.  It is independent of batching,
/// concurrency and storage tuning: those change execution, not what an assertion means.
/// </summary>
public sealed class SemanticSourceRecipe
{
    private readonly IReadOnlyDictionary<string, SourceRecipeField> _fields;
    private readonly IReadOnlyList<SourceRecipeField> _fieldList;
    private readonly IReadOnlyDictionary<string, SourceRecipeStructure> _structures;

    public SemanticSourceRecipe(
        string authority,
        string release,
        string provider,
        string syntax,
        IEnumerable<SourceRecipeField> fields,
        IEnumerable<SourceRecipeStructure>? structures = null)
    {
        Authority = Required(authority, nameof(authority));
        Release = Required(release, nameof(release));
        Provider = Required(provider, nameof(provider));
        Syntax = Required(syntax, nameof(syntax));

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

        CanonicalForm = BuildCanonicalForm(fieldArray, Structures);
        RecipeId = Hash128.OfCanonical(CanonicalForm);
    }

    public string Authority { get; }
    public string Release { get; }
    public string Provider { get; }
    public string Syntax { get; }
    public IReadOnlyList<SourceRecipeField> Fields => _fieldList;
    public IReadOnlyList<SourceRecipeStructure> Structures { get; }
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

    private string BuildCanonicalForm(
        IEnumerable<SourceRecipeField> fields,
        IEnumerable<SourceRecipeStructure> structures)
    {
        var canonical = new StringBuilder("laplace/source-recipe/v1");
        Append(canonical, Authority);
        Append(canonical, Release);
        Append(canonical, Provider);
        Append(canonical, Syntax);
        foreach (SourceRecipeField field in fields.OrderBy(static f => f.SyntaxPath, StringComparer.Ordinal))
        {
            canonical.Append("|f");
            Append(canonical, field.SyntaxPath);
            Append(canonical, field.PropertyName);
            Append(canonical, field.ValueKind.ToString());
            Append(canonical, ((int)field.Disposition).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(canonical, field.AbsentSentinel ?? "");
            Append(canonical, field.SequenceSeparator ?? "");
        }
        foreach (SourceRecipeStructure structure in structures)
        {
            canonical.Append("|s");
            Append(canonical, structure.SyntaxPath);
            Append(canonical, structure.SemanticType);
            Append(canonical, ((int)structure.Disposition).ToString(System.Globalization.CultureInfo.InvariantCulture));
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
    }

    private static void ValidateDisposition(SourceFieldDisposition disposition, string syntaxPath)
    {
        if (disposition == SourceFieldDisposition.None)
            throw new ArgumentException($"Field or structure '{syntaxPath}' has no disposition.");
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
    IReadOnlyList<string> Sequence);

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
        return new SourceRecipeValue(raw, absent, boolean, integer, sequence);
    }
}
