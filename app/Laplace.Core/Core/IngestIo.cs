namespace Laplace.Engine.Core;

/// <summary>
/// Shared file-opening policy for ingest. Callers own their resource-derived read
/// buffers, so the stream's internal buffer is deliberately disabled instead of
/// layering a second scattered 4 KiB/64 KiB/1 MiB tuning value beneath them.
/// </summary>
public static class IngestIo
{
    public static FileStream OpenSequentialRead(
        string path, bool useAsync = false, FileShare share = FileShare.Read)
        => new(
            path, FileMode.Open, FileAccess.Read, share,
            bufferSize: 1,
            options: FileOptions.SequentialScan
                | (useAsync ? FileOptions.Asynchronous : FileOptions.None));
}
