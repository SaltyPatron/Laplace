namespace Laplace.Decomposers.Abstractions;

public sealed class CiliMapMissingException : Exception
{
    public string ExpectedPath { get; }

    public CiliMapMissingException(string expectedPath, string sourceName)
        : base(BuildMessage(expectedPath, sourceName))
    {
        ExpectedPath = expectedPath;
    }

    private static string BuildMessage(string expectedPath, string sourceName) =>
        $"CILI ILI map is required for {sourceName} ingest but is missing or empty. " +
        $"Expected file at {expectedPath}. " +
        $"Set LAPLACE_CILI_DIR to the directory containing {IliMap.MapFileName}, " +
        $"or place the file under {{LAPLACE_DATA_ROOT}}/CILI/.";
}
