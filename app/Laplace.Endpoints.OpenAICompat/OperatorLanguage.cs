using System.Globalization;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The operator's language tag as written: an explicit request field, else the
/// highest-quality Accept-Language entry, else the host culture. The tag's entity is
/// its content id; ISO 639 testimony in the substrate relates it to other
/// tags, so no tag is rewritten or refused here.
/// </summary>
internal readonly record struct OperatorLanguage(string Code, byte[] Id, string Source)
{
    public static OperatorLanguage? Resolve(HttpRequest request, string? explicitLanguage)
    {
        if (LanguageReference.ResolveCode(explicitLanguage) is { } requested)
            return FromCode(requested, "request");

        foreach (var candidate in AcceptLanguageCandidates(request.Headers.AcceptLanguage.ToString()))
            if (LanguageReference.ResolveCode(candidate) is { } accepted)
                return FromCode(accepted, "accept-language");

        if (LanguageReference.ResolveSystemCode() is { } systemCode)
            return FromCode(systemCode, "system");

        // An invariant or unconfigured host has no honest default.  SQL can still
        // infer the prompt language, so absence remains absence instead of becoming
        // an invented English preference.
        return null;
    }

    private static OperatorLanguage FromCode(string code, string source) =>
        new(code, LanguageReference.IdForResolvedCode(code).ToBytes(), source);

    private static IEnumerable<string> AcceptLanguageCandidates(string header)
    {
        return header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((part, ordinal) =>
            {
                var fields = part.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                double quality = 1.0;
                foreach (var field in fields.Skip(1))
                    if (field.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(field.AsSpan(2), NumberStyles.AllowDecimalPoint,
                            CultureInfo.InvariantCulture, out var q))
                        quality = q;
                return (Language: fields[0], Quality: quality, Ordinal: ordinal);
            })
            .Where(x => x.Language != "*" && x.Quality > 0)
            .OrderByDescending(x => x.Quality)
            .ThenBy(x => x.Ordinal)
            .Select(x => x.Language);
    }

}
