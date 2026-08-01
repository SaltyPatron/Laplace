namespace Laplace.Decomposers.OMW;

internal static class OmwIngestSupport
{
    internal static string LangFromLabel(string fileLabel)
    {
        int slash = fileLabel.LastIndexOf('/');
        return slash >= 0 && slash + 1 < fileLabel.Length
            ? fileLabel[(slash + 1)..]
            : "und";
    }
}
