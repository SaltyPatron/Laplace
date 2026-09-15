using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Operational;

/// <summary>
/// Admits the selected repository contract artifacts unchanged through the shared
/// full-source grammar/file pipeline. It contributes source structure and provenance;
/// it does not manufacture lexical aliases or compile documented ISA descriptions.
/// </summary>
public sealed class OperationalDecomposer
    : GrammarComposeDecomposerMultiFile<OperationalSource, FullScope>,
      IIngestInventoryProvider, IIngestArtifactGraphProvider
{
    public override int LayerOrder => 2;
    protected override double SourceTrust => Abstractions.SourceTrust.SubstrateMandate;
    public override bool PerFileCompletion => true;

    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "seeds", "operational");

    private static string? ModalityFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".md" or ".txt" => "markdown",
        ".json" => "json",
        _ => null,
    };

    private static IReadOnlyList<string> InputFiles(string root)
    {
        if (File.Exists(root))
        {
            if (ModalityFor(root) is null)
                throw new InvalidDataException("Operational artifacts must be .md/.txt contracts or declared .json task shapes.");
            return [Path.GetFullPath(root)];
        }
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Operational contract source does not exist: {root}");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => ModalityFor(p) is not null && !VendoredPathFilter.IsVendoredOrBuildPath(p, root))
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
            throw new InvalidDataException($"No operational contract artifacts were found in {root}.");
        return files;
    }

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options) => InputFiles(ecosystemPath)
            .Select(p => (p, "operational/" + (File.Exists(ecosystemPath)
                ? Path.GetFileName(p)
                : Path.GetRelativePath(ecosystemPath, p).Replace('\\', '/')))).ToArray();

    protected override async IAsyncEnumerable<GrammarComposeRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string relative = fileLabel.StartsWith("operational/", StringComparison.Ordinal)
            ? fileLabel["operational/".Length..] : Path.GetFileName(filePath);
        yield return await ReadContractAsync(filePath, relative, ct);
    }

    internal static async Task<GrammarComposeRecord> ReadContractAsync(
        string filePath, string relativePath, CancellationToken ct = default)
    {
        string modality = ModalityFor(filePath)
            ?? throw new InvalidDataException($"Unsupported operational source artifact: {filePath}");
        byte[] bytes = await File.ReadAllBytesAsync(filePath, ct);
        if (bytes.Length == 0)
            throw new InvalidDataException($"Operational source contract is empty: {filePath}");
        return new GrammarComposeRecord(bytes, modality,
            FileMetadata: GrammarSourceFileSupport.MetadataFromPath(filePath, relativePath, modality),
            StructureWitness: modality == "json" ? OperationalTaskShapeWitness.Instance : null);
    }

    public Task<IngestArtifactGraph?> DescribeArtifactsAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _ = InputFiles(ecosystemPath);
        return Task.FromResult(GrammarSourceFileSupport.BuildArtifactGraph(
            ecosystemPath, OperationalSource.SourceName, "operational", ModalityFor));
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var files = context.HasArtifactGraph
            ? context.SelectedArtifacts.Select(a => new IngestFileSpec(a.FileLabel, a.Path, 1)).ToArray()
            : ListFiles(context.EcosystemPath, options)
                .Select(f => new IngestFileSpec(f.Label, f.Path, 1)).ToArray();
        long total = options.MaxInputUnits > 0 ? Math.Min(files.Length, options.MaxInputUnits) : files.Length;
        return Task.FromResult<IngestInventory?>(
            new IngestInventory("source contracts", total, files, TracksFileCompletion: true));
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default) =>
        (await DescribeInputAsync(context, DecomposerOptions.Default, ct))?.TotalInputUnits;
}
