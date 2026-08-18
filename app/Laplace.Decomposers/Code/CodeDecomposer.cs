using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Extractors;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Code;

public sealed class CodeDecomposer : GrammarComposeDecomposerMultiFile<CodeSource, FullScope>, IIngestInventoryProvider
{
    private static readonly HashSet<string> TemplateSuffixes =
        new(StringComparer.OrdinalIgnoreCase) { ".in" };

    public static readonly Hash128 Source = CodeSource.SourceId;
    public static readonly Hash128 TrustClass = CodeSource.TrustClass;

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "code";

    public override bool PerFileCompletion => true;

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        bool rootIsFile = File.Exists(ecosystemPath);
        return EnumerateCodeFiles(ecosystemPath)
            .Select(x =>
            {
                string rel = rootIsFile
                    ? Path.GetFileName(x.File)
                    : Path.GetRelativePath(ecosystemPath, x.File).Replace('\\', '/');
                return (x.File, $"code/{rel}");
            })
            .ToList();
    }

    protected override async IAsyncEnumerable<GrammarComposeRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? modality = ModalityOf(filePath);
        if (modality is null) yield break;
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(filePath, ct); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"CodeDecomposer: failed to read '{filePath}': {ex.Message}", ex);
        }
        if (bytes.Length == 0) yield break;
        yield return new GrammarComposeRecord(bytes, modality, SourceId: FileEntity.SourceId(bytes));
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EnumerateCodeFiles(context.EcosystemPath).Count());

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateCodeFiles(context.EcosystemPath).Select(x => x.File).ToList();
        return Task.FromResult(IngestInventory.FromFileUnits(
            "source files", paths, options.MaxInputUnits, tracksFileCompletion: true));
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
