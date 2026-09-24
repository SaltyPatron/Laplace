using System.Collections.Concurrent;
using System.Globalization;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// A language tag is content: the entity of a tag is the content id of the tag exactly
/// as the source wrote it. "en", "eng" and "English" are three entities; ISO 639 testimony
/// (HAS_ISO639_1_CODE, NAME_ALIAS, ...) relates them, and traversal follows that testimony.
/// No code is rewritten into another code here.
/// </summary>
public static class LanguageReference
{
    private static long _resolveMisses;

    private static readonly ConcurrentDictionary<string, Hash128> IdByCode =
        new(StringComparer.Ordinal);

    public static long ResolveMisses => Interlocked.Read(ref _resolveMisses);

    /// <summary>The tag as written (surrounding whitespace removed), or null when absent.</summary>
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

    /// <summary>Content id of a tag; an absent tag is the content "und" (counted as a miss).</summary>
    public static Hash128 IdForResolvedCode(string? code)
    {
        if (code is null) { Interlocked.Increment(ref _resolveMisses); code = "und"; }
        return IdByCode.GetOrAdd(code, static c => ContentEmitter.RootId(c)
            ?? throw new InvalidOperationException($"language tag could not be composed: {c}"));
    }

    public static Hash128 Emit(
        SubstrateChangeBuilder builder,
        string? input,
        Hash128 sourceId,
        double sourceTrust) =>
        EmitResolvedCode(builder, ResolveCode(input), sourceId, sourceTrust);

    public static Hash128 EmitResolvedCode(
        SubstrateChangeBuilder builder,
        string? code,
        Hash128 sourceId,
        double sourceTrust)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (code is null)
        {
            Interlocked.Increment(ref _resolveMisses);
            code = "und";
        }
        string tag = code.Trim();
        Hash128 id = ContentEmitter.Emit(builder, tag, sourceId)
            ?? throw new InvalidOperationException(
                $"language tag could not be admitted as content: {tag}");
        CategoryAnchor.AttestCategory(
            builder, id, EntityTypeRegistry.Language, sourceId, sourceTrust);
        IdByCode.TryAdd(tag, id);
        return id;
    }
}
