namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// FrameNet LU name → surface lemma. Shared by FrameNetDecomposer and FrameNetLuIngest.
/// </summary>
public static class FrameNetLemmaHelper
{
    public static string LemmaOf(string luName)
    {
        int dot = luName.LastIndexOf('.');
        return (dot > 0 ? luName[..dot] : luName).Trim();
    }

    public static string CollapseWs(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool ws = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { ws = true; continue; }
            if (ws && sb.Length > 0) sb.Append(' ');
            ws = false;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
