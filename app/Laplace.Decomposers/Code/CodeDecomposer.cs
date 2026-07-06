using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Code;

public sealed class CodeDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/CodeDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    public Hash128 SourceId => Source;
    public string SourceName => "CodeDecomposer";
    public int LayerOrder => 2;
    public Hash128 TrustClassId => TrustClass;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            relationNodeNames: ["CALLS", "DEFINES", "REFERENCES"],
            ct: ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var files = EnumerateCodeFiles(context.EcosystemPath).ToList();
        if (files.Count == 0) yield break;
        int batch = options.BatchSize > 1 ? options.BatchSize : 512;

        await foreach (var change in GrammarComposeIngestSupport.RunAsync(
                           EnumerateRecordsAsync(files, ct), Source, SourceTrust.StructuredCorpus,
                           "code", batch, context.Reader, options, ct))
            yield return change;
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EnumerateCodeFiles(context.EcosystemPath).Count());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async IAsyncEnumerable<GrammarComposeRecord> EnumerateRecordsAsync(
        IReadOnlyList<(string File, string Modality)> files,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var (file, modality) in files)
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, ct); }
            catch { continue; }
            if (bytes.Length == 0) continue;
            yield return new GrammarComposeRecord(bytes, modality);
        }
    }

    private static IEnumerable<(string File, string Modality)> EnumerateCodeFiles(string root)
    {
        if (File.Exists(root))
        {
            var m = ModalityOf(root);
            if (m is not null) yield return (root, m);
            yield break;
        }
        if (!Directory.Exists(root)) yield break;
        char sep = Path.DirectorySeparatorChar;
        string objSeg = $"{sep}obj{sep}", binSeg = $"{sep}bin{sep}", gitSeg = $"{sep}.git{sep}";
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                                      .OrderBy(p => p, StringComparer.Ordinal))
        {
            if (file.Contains(objSeg) || file.Contains(binSeg) || file.Contains(gitSeg)) continue;
            var m = ModalityOf(file);
            if (m is not null) yield return (file, m);
        }
    }

    private static string? ModalityOf(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Length > 0 && ext[0] == '.') ext = ext[1..];
        return ext.Length == 0 ? null : GrammarDecomposer.ModalityByExt(ext.ToLowerInvariant());
    }
}
