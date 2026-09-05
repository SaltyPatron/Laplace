using System.Text;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Bounded parser for Project Gutenberg's text header grammar. It accepts both
/// classic <c>Key: value</c> headers and the current HTML-derived
/// <c>Key\n: value</c> form. The original bytes remain the file content; this parser
/// only supplies identity/query fields for the file metadata branch.
/// </summary>
public static class ProjectGutenbergMetadata
{
    private const int HeaderProbeBytes = 64 * 1024;
    public const string FormatName = "project-gutenberg-text";

    public static DocumentFormatMetadata? Extract(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return null;
        int length = Math.Min(utf8.Length, HeaderProbeBytes);
        while (length > 0 && length < utf8.Length && (utf8[length] & 0xc0) == 0x80)
            length--;
        string prefix = Encoding.UTF8.GetString(utf8[..length]);
        if (!RecognizesFormat(prefix)) return null;
        bool boundaryExpected = HasStartBoundary(prefix);

        string? title = null, author = null, language = null, release = null;
        string? updated = null, credits = null, boundary = null;
        long? boundaryOffset = null;
        string? current = null;
        bool allowPlainContinuation = false;
        bool metadataEnded = false;
        bool formalHeaderStarted = false;
        bool metadataStarted = false;
        int headerEnd = prefix.Length;

        int at = 0;
        while (at <= prefix.Length)
        {
            int end = prefix.IndexOf('\n', at);
            if (end < 0) end = prefix.Length;
            string raw = prefix[at..end].TrimEnd('\r');
            string trimmed = raw.Trim().TrimStart('\ufeff');

            if (IsFormalBanner(trimmed)) formalHeaderStarted = true;

            if (IsStartBoundary(trimmed))
            {
                boundary = raw;
                boundaryOffset = Encoding.UTF8.GetByteCount(prefix.AsSpan(0, at));
                headerEnd = end;
                break;
            }

            if (!formalHeaderStarted)
            {
                if (end == prefix.Length) break;
                at = end + 1;
                continue;
            }

            if (!metadataEnded && TryField(trimmed, out string? key, out string? value,
                    out bool splitKey))
            {
                metadataStarted = true;
                current = key;
                allowPlainContinuation = splitKey;
                if (value is { Length: > 0 })
                    Assign(key!, value, ref title, ref author, ref language, ref release,
                        ref updated, ref credits);
            }
            else if (!metadataEnded && current is not null && trimmed.StartsWith(':'))
            {
                string valueAfterColon = trimmed[1..].Trim();
                if (valueAfterColon.Length > 0)
                {
                    Assign(current, valueAfterColon, ref title, ref author, ref language,
                        ref release, ref updated, ref credits);
                    if (current is not "title" and not "author")
                        allowPlainContinuation = false;
                }
            }
            else if (!metadataEnded && current is not null && trimmed.Length > 0
                     && (char.IsWhiteSpace(raw[0]) || allowPlainContinuation))
            {
                Append(current, trimmed, ref title, ref author, ref language, ref release,
                    ref updated, ref credits);
                allowPlainContinuation = false;
            }
            else if (trimmed.Length > 0)
            {
                if (metadataStarted && !boundaryExpected)
                {
                    metadataEnded = true;
                    headerEnd = at;
                    break;
                }
                current = null;
                allowPlainContinuation = false;
            }

            if (end == prefix.Length) break;
            at = end + 1;
        }

        string? ebookId = FindEbookId(prefix[..headerEnd]);
        string headerStatus = boundary is not null || metadataEnded || utf8.Length <= length
            ? "complete"
            : "incomplete-probe-limit";
        return new DocumentFormatMetadata(
            FormatName, ebookId, Clean(title), Clean(author), Clean(language),
            Clean(release), Clean(updated), Clean(credits), boundaryOffset, boundary,
            headerStatus);
    }

    private static bool TryField(
        string line, out string? key, out string? value, out bool splitKey)
    {
        string candidate = line.StartsWith('[') && line.EndsWith(']')
            ? line[1..^1]
            : line;
        int colon = candidate.IndexOf(':');
        string label = colon >= 0 ? candidate[..colon].Trim() : candidate.Trim();
        key = CanonicalKey(label);
        value = colon >= 0 ? candidate[(colon + 1)..].Trim() : null;
        splitKey = colon < 0 && key is not null;
        return key is not null;
    }

    private static string? CanonicalKey(string label)
    {
        if (label.Equals("Title", StringComparison.OrdinalIgnoreCase)) return "title";
        if (label.Equals("Author", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Creator", StringComparison.OrdinalIgnoreCase)) return "author";
        if (label.Equals("Language", StringComparison.OrdinalIgnoreCase)) return "language";
        if (label.Equals("Release date", StringComparison.OrdinalIgnoreCase)) return "release";
        if (label.Equals("Most recently updated", StringComparison.OrdinalIgnoreCase)) return "updated";
        if (label.Equals("Credits", StringComparison.OrdinalIgnoreCase)) return "credits";

        // Known Gutenberg header fields delimit a preceding split-form value even
        // though they are outside the selected inventory contract.
        if (label.Equals("Contributor", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Subtitle", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Author of introduction, etc.", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Translator", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Editor", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Original publication", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Other information and formats", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Character set encoding", StringComparison.OrdinalIgnoreCase))
            return "delimiter";
        return null;
    }

    private static void Assign(
        string key, string value,
        ref string? title, ref string? author, ref string? language,
        ref string? release, ref string? updated, ref string? credits)
    {
        value = CollapseWhitespace(value);
        if (value.Length == 0) return;
        switch (key)
        {
            case "title": title ??= value; break;
            case "author": author = JoinDistinct(author, value); break;
            case "language": language ??= value; break;
            case "release": release ??= value; break;
            case "updated": updated ??= value; break;
            case "credits": credits ??= value; break;
        }
    }

    private static void Append(
        string key, string value,
        ref string? title, ref string? author, ref string? language,
        ref string? release, ref string? updated, ref string? credits)
    {
        value = CollapseWhitespace(value);
        if (value.Length == 0) return;
        switch (key)
        {
            case "title": title = Join(title, value); break;
            case "author": author = Join(author, value); break;
            case "language": language = Join(language, value); break;
            case "release": release = Join(release, value); break;
            case "updated": updated = Join(updated, value); break;
            case "credits": credits = Join(credits, value); break;
        }
    }

    private static string Join(string? current, string value) =>
        current is null ? value : $"{current} {value}";

    private static string JoinDistinct(string? current, string value) =>
        current is null || current.Equals(value, StringComparison.Ordinal)
            ? current ?? value
            : $"{current}; {value}";

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : CollapseWhitespace(value);

    internal static string CollapseWhitespace(string value)
    {
        var result = new StringBuilder(value.Length);
        bool pending = false;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pending = result.Length > 0;
                continue;
            }
            if (pending) result.Append(' ');
            result.Append(rune);
            pending = false;
        }
        return result.ToString();
    }

    private static bool IsStartBoundary(string line) =>
        line.StartsWith("*** START OF THE PROJECT GUTENBERG", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("*** START OF THIS PROJECT GUTENBERG", StringComparison.OrdinalIgnoreCase);

    private static bool IsFormalBanner(string line) =>
        line.StartsWith("The Project Gutenberg eBook", StringComparison.OrdinalIgnoreCase);

    private static bool HasStartBoundary(string prefix)
    {
        int at = 0;
        while (at <= prefix.Length)
        {
            int end = prefix.IndexOf('\n', at);
            if (end < 0) end = prefix.Length;
            if (IsStartBoundary(prefix[at..end].Trim())) return true;
            if (end == prefix.Length) break;
            at = end + 1;
        }
        return false;
    }

    private static bool RecognizesFormat(string prefix)
    {
        int at = 0;
        int nonempty = 0;
        while (at <= prefix.Length && nonempty < 8)
        {
            int end = prefix.IndexOf('\n', at);
            if (end < 0) end = prefix.Length;
            string line = prefix[at..end].Trim().TrimStart('\ufeff');
            if (line.Length > 0)
            {
                nonempty++;
                if (IsFormalBanner(line)
                    || IsStartBoundary(line)
                    || line.EndsWith("| Project Gutenberg", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("%% Project Gutenberg's ", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            if (end == prefix.Length) break;
            at = end + 1;
        }
        return false;
    }

    private static string? FindEbookId(string header)
    {
        string[] markers = ["[eBook #", "/ebooks/", "PROJECT GUTENBERG EBOOK "];
        foreach (string marker in markers)
        {
            int start = header.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            start += marker.Length;
            int end = start;
            while (end < header.Length && char.IsAsciiDigit(header[end])) end++;
            if (end > start) return header[start..end];
        }
        return null;
    }
}
