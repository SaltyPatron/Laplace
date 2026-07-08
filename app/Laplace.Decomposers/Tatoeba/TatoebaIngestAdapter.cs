using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Tatoeba;

public sealed class TatoebaMultiFileStream : IMultiFileRecordStream<GrammarIngestRecord>
{
    private readonly IReadOnlyList<(string Path, string Label, Func<ReadOnlySpan<byte>, bool>? AcceptRow)> _files;

    public TatoebaMultiFileStream(
        IReadOnlyList<(string Path, string Label, Func<ReadOnlySpan<byte>, bool>? AcceptRow)> files) =>
        _files = files;

    public async IAsyncEnumerable<(string FileLabel, GrammarIngestRecord Record)> RecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var source = EtlManifest.Get("tatoeba");
        foreach (var (path, label, acceptRow) in _files)
        {
            ct.ThrowIfCancellationRequested();
            var stream = GrammarFileRecordStream.ForSource(path, source, acceptRow);
            await foreach (var record in stream.RecordsAsync(ct))
                yield return (label, record);
        }
    }
}
