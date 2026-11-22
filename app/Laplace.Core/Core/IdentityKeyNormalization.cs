using System.Text;

namespace Laplace.Engine.Core;

public static class IdentityKeyNormalization
{
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(normalized.Length);
        bool inSpace = true;
        foreach (Rune r in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(r))
            {
                if (!inSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    inSpace = true;
                }
                continue;
            }
            inSpace = false;
            var s = r.ToString().ToLowerInvariant();
            sb.Append(s);
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }
}
