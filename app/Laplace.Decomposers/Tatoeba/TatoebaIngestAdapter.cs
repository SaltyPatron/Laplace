using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Tatoeba;

public sealed class TatoebaMultiFileStream : IMultiFileRecordStream<GrammarIngestRecord>
{
    private readonly IReadOnlyList<(string Path, string Label, Func<ReadOnlySpan<byte>, bool>? AcceptRow)> _files;

    public TatoebaMultiFileStream(
        IReadOnlyList<(string Path, string Label, Func<ReadOnlySpan<byte>, bool>? AcceptRow)> files) =>
        _files = files;

    public async IAsyncEnumerable<IFileRecordSource<GrammarIngestRecord>> FilesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var source = EtlManifest.Get("tatoeba");
        foreach (var (path, label, acceptRow) in _files)
        {
            ct.ThrowIfCancellationRequested();
            string p = path; var accept = acceptRow;
            yield return new DelegateFileRecordSource<GrammarIngestRecord>(
                label, token => OpenAsync(p, source, accept, token));
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<GrammarIngestRecord> OpenAsync(
        string path, EtlSource source, Func<ReadOnlySpan<byte>, bool>? acceptRow,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var stream = GrammarFileRecordStream.ForSource(path, source, acceptRow);
        await foreach (var record in stream.RecordsAsync(ct))
            yield return record;
    }
}
