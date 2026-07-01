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
}
