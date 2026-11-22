using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

internal static class EntityTiers
{
    public const byte Codepoint = 0;
    public const byte Grapheme = 1;
    public const byte Word = 2;
    public const byte Sentence = 3;
    public const byte Document = 4;
}

internal static partial class ConversationContent
{
    [GeneratedRegex(@"^[A-Za-z0-9._@-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    public static bool IsValidIdentifier(string? value) =>
        value is not null && IdentifierRegex().IsMatch(value);

    public static Hash128 SessionId(string tenant, string sessionKey)
    {
        if (!IsValidIdentifier(tenant))
            throw new ArgumentException($"tenant '{tenant}' is not a valid identifier", nameof(tenant));
        if (!IsValidIdentifier(sessionKey))
            throw new ArgumentException($"session key '{sessionKey}' is not a valid identifier", nameof(sessionKey));
        return SubstrateCanonicalIds.ConversationSession(tenant, sessionKey);
    }
}

internal static class LanguageReference
{
    private static readonly ConcurrentDictionary<string, Hash128> IdByCode = new(StringComparer.Ordinal);

    public static string? ResolveCode(string? input) =>
        string.IsNullOrWhiteSpace(input) ? null : input.Trim();

    public static string? ResolveSystemCode()
    {
        foreach (var candidate in SystemLanguageCandidates())
            if (ResolveCode(candidate) is { } code)
                return code;
        return null;
    }

    private static IEnumerable<string> SystemLanguageCandidates()
    {
        if (!string.IsNullOrWhiteSpace(CultureInfo.CurrentUICulture.Name))
            yield return CultureInfo.CurrentUICulture.Name;
        if (!string.IsNullOrWhiteSpace(CultureInfo.CurrentCulture.Name)
            && CultureInfo.CurrentCulture.Name != CultureInfo.CurrentUICulture.Name)
            yield return CultureInfo.CurrentCulture.Name;

        var locale = Environment.GetEnvironmentVariable("LANG");
        if (!string.IsNullOrWhiteSpace(locale))
        {
            int suffix = locale.IndexOfAny(['.', '@']);
            yield return suffix > 0 ? locale[..suffix] : locale;
        }
    }

    public static Hash128 Resolve(string? input) => IdForResolvedCode(ResolveCode(input));

    public static Hash128 IdForResolvedCode(string? code)
    {
        var tag = code ?? "und";
        return IdByCode.GetOrAdd(tag, static c => Hash128.Text(c));
    }
}

internal static class RelationTypeCatalog
{
    public enum Symmetry { Asymmetric, Symmetric }

    public readonly record struct RelationTypeEntry(
        Hash128 Id, double Rank, Symmetry Symmetry, string Canonical);

    private static readonly (string Name, double Rank, Symmetry Symmetry)[] CanonicalRelations =
    [
        ("IS_A", 1.0, Symmetry.Asymmetric),
        ("PART_OF", 0.95, Symmetry.Asymmetric),
        ("HAS_A", 0.95, Symmetry.Asymmetric),
        ("CAUSES", 0.90, Symmetry.Asymmetric),
        ("USED_FOR", 0.85, Symmetry.Asymmetric),
        ("CAPABLE_OF", 0.85, Symmetry.Asymmetric),
        ("AT_LOCATION", 0.85, Symmetry.Asymmetric),
        ("DERIVED_FROM", 0.85, Symmetry.Asymmetric),
        ("SYNONYM", 0.90, Symmetry.Symmetric),
        ("SIMILAR_TO", 0.80, Symmetry.Symmetric),
        ("ANTONYM", 0.90, Symmetry.Symmetric),
        ("TRANSLATION_OF", 0.95, Symmetry.Symmetric),
    ];

    public static IEnumerable<RelationTypeEntry> AllCanonical()
    {
        foreach (var (name, rank, symmetry) in CanonicalRelations)
            yield return new RelationTypeEntry(Hash128.Text(name), rank, symmetry, name);
    }
}

internal static class GrammarSourceSupport
{
    public static GrammarAstDiagnostics RequireNativeSourceAst(GrammarAst ast)
    {
        var diagnostics = ast.Diagnostics;
        if (diagnostics.AstNodeCount == 0 || ast.GetNode(0).Parent != GrammarAst.Root)
            throw new InvalidDataException("Native source grammar produced no rooted AST.");
        return diagnostics;
    }
}
