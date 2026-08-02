using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Extractors;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Code;

public sealed class CodeDecomposer : GrammarComposeDecomposer<CodeSource, FullScope>
{
    private static readonly HashSet<string> TemplateSuffixes =
        new(StringComparer.OrdinalIgnoreCase) { ".in" };

    public static readonly Hash128 Source = CodeSource.SourceId;
    public static readonly Hash128 TrustClass = CodeSource.TrustClass;

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "code";

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
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                                      .OrderBy(p => p, StringComparer.Ordinal))
        {
            if (VendoredPathFilter.IsVendoredOrBuildPath(file)) continue;
            var m = ModalityOf(file);
            if (m is not null) yield return (file, m);
        }
    }

    internal static string? ModalityOf(string path)
    {
        string ext = Path.GetExtension(path);
        var modality = ResolveExtension(ext);
        if (modality is not null || !TemplateSuffixes.Contains(ext))
            return modality;

        // A trailing template marker has no modality of its own. Resolve the
        // preceding extension instead (`chat.sql.in` -> SQL); never map `.in`
        // globally, because non-SQL template inputs use the same suffix.
        string stem = path[..^ext.Length];
        return ResolveExtension(Path.GetExtension(stem));
    }

    private static string? ResolveExtension(string ext)
        => ext.Length <= 1 || ext[0] != '.'
            ? null
            : GrammarDecomposer.ModalityByExt(ext[1..].ToLowerInvariant());
}
