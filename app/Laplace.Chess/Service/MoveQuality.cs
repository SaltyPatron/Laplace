namespace Laplace.Chess.Service;

internal static class MoveQuality
{
    public static string? FromStream(PgnMovetext.PgnMoveStream ply)
    {
        if (ply.Nag is { } nag && FromNag(nag) is { } n) return n;
        if (ply.StandaloneAnnotation is { } sa && FromSuffix(sa) is { } s) return s;
        if (ply.SuffixAnnotation is { } su && FromSuffix(su) is { } t) return t;
        return null;
    }

    public static string? FromNag(int nag) => nag switch
    {
        1 => "good",
        2 => "mistake",
        3 => "brilliant",
        4 => "blunder",
        5 => "interesting",
        6 => "dubious",
        _ => null,
    };

    public static string? FromSuffix(string glyph) => glyph switch
    {
        "!" => "good",
        "!!" => "brilliant",
        "?" => "mistake",
        "??" => "blunder",
        "!?" => "interesting",
        "?!" => "dubious",
        _ => null,
    };

    public static string? FromReviewTag(string tag) => tag switch
    {
        "blunder" => "blunder",
        "mistake" => "mistake",
        "inaccuracy" => "inaccuracy",
        _ => null,
    };

    public static string? FromSerializedAnnotations(string? annotations)
    {
        if (string.IsNullOrWhiteSpace(annotations)) return null;
        foreach (var token in annotations.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length > 1 && token[0] == '$'
                && int.TryParse(token.AsSpan(1), out int nag)
                && FromNag(nag) is { } fromNag)
                return fromNag;
            if (FromSuffix(token) is { } fromSuffix) return fromSuffix;
        }
        return null;
    }
}
