using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.OMW;

public sealed class OmwMultiFileStream : IMultiFileRecordStream<GrammarIngestRecord>
{
    private readonly IReadOnlyList<(string Path, string Label, string Lang)> _files;

    public OmwMultiFileStream(IReadOnlyList<(string Path, string Label, string Lang)> files) => _files = files;

    public async IAsyncEnumerable<(string FileLabel, GrammarIngestRecord Record)> RecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var omw = EtlManifest.Get("omw");
        foreach (var (path, label, _) in _files)
        {
            ct.ThrowIfCancellationRequested();
            var stream = GrammarFileRecordStream.ForSource(
                path, omw, static line => line.Length > 0 && line[0] != (byte)'#');

            await using var e = stream.RecordsAsync(ct).GetAsyncEnumerator(ct);
            while (true)
            {
                GrammarIngestRecord record;
                try
                {
                    if (!await e.MoveNextAsync()) break;
                    record = e.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"OMW ingest failed in \"{path}\": {ex.Message}", ex);
                }
                yield return (label, record);
            }
        }
    }
}

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
