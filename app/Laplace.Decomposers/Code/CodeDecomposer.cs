using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Extractors;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Code;

public sealed class CodeDecomposer : GrammarComposeDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/CodeDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    public override Hash128 SourceId => Source;
    public override string SourceName => "CodeDecomposer";
    public override int LayerOrder => 2;
    public override Hash128 TrustClassId => TrustClass;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "code";

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            relationNodeNames: ["CALLS", "DEFINES", "REFERENCES"],
            ct: ct);

    protected override async IAsyncEnumerable<GrammarComposeRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var files = EnumerateCodeFiles(ecosystemPath).ToList();
        foreach (var (file, modality) in files)
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, ct); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"CodeDecomposer: failed to read '{file}': {ex.Message}", ex);
            }
            if (bytes.Length == 0) continue;
            yield return new GrammarComposeRecord(bytes, modality);
        }
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EnumerateCodeFiles(context.EcosystemPath).Count());

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
