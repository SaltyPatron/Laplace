namespace Laplace.Engine.Core;

/// <summary>
/// Shared file-opening policy for ingest. Callers own their resource-derived read
/// buffers, so the stream's internal buffer is disabled rather than layered beneath them.
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
