using System.Globalization;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Endpoints.OpenAICompat;

internal readonly record struct OperatorLanguage(string Code, byte[] Id, string Source)
{
    public static bool TryResolve(
        HttpRequest request,
        string? explicitLanguage,
        out OperatorLanguage? language,
        out string? invalidExplicitLanguage)
    {
        LanguageReference.EnsureLoaded();
        language = null;
        invalidExplicitLanguage = null;

        if (!string.IsNullOrWhiteSpace(explicitLanguage))
        {
            var code = LanguageReference.ResolveCode(explicitLanguage);
            if (code is null)
            {
                invalidExplicitLanguage = explicitLanguage.Trim();
                return false;
            }

            language = FromCode(code, "request");
            return true;
        }

        foreach (var candidate in AcceptLanguageCandidates(request.Headers.AcceptLanguage.ToString()))
        {
            var code = LanguageReference.ResolveCode(candidate);
            if (code is null) continue;
            language = FromCode(code, "accept-language");
            return true;
        }

        if (LanguageReference.ResolveSystemCode() is { } systemCode)
        {
            language = FromCode(systemCode, "system");
            return true;
        }

        // An invariant or unconfigured host has no honest default.  SQL can still
        // infer the prompt language, so absence remains absence instead of becoming
        // an invented English preference.
        return true;
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
