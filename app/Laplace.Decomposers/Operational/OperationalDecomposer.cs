using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.UD;
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

    private readonly ConcurrentDictionary<string, byte> _canonicalNames = new(StringComparer.Ordinal);
    private ConcurrentIdSet _seenSourceDeclarations = new();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames.Keys.ToArray();
    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => _canonicalNames;

    protected override Task OnBeforeRegisterAsync(IDecomposerContext context, CancellationToken ct)
    {
        _seenSourceDeclarations = new ConcurrentIdSet();
        return Task.CompletedTask;
    }

    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "seeds", "operational");

    private static string? ModalityFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".md" or ".txt" or ".conllu" => "markdown",
        ".json" => "json",
        _ => null,
    };

    private static IReadOnlyList<string> InputFiles(string root)
    {
        if (File.Exists(root))
        {
            if (ModalityFor(root) is null)
                throw new InvalidDataException("Operational artifacts must be .md/.txt contracts, authored .conllu annotations, or declared .json task shapes.");
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
        yield return await ReadContractAsync(filePath, relative, ct, _canonicalNames, _seenSourceDeclarations);
    }

    internal static async Task<GrammarComposeRecord> ReadContractAsync(
        string filePath, string relativePath, CancellationToken ct = default,
        ConcurrentDictionary<string, byte>? canonicalNames = null,
        ConcurrentIdSet? seenSourceDeclarations = null)
    {
        string modality = ModalityFor(filePath)
            ?? throw new InvalidDataException($"Unsupported operational source artifact: {filePath}");
        byte[] bytes = await File.ReadAllBytesAsync(filePath, ct);
        if (bytes.Length == 0)
            throw new InvalidDataException($"Operational source contract is empty: {filePath}");
        IGrammarWitness? witness = modality == "json" ? OperationalTaskShapeWitness.Instance : null;
        if (Path.GetExtension(filePath).Equals(".conllu", StringComparison.OrdinalIgnoreCase))
        {
            string languageCode = UdIngestSupport.ExtractLangCode(Path.GetFileName(filePath));
            if (languageCode == "und")
                throw new InvalidDataException("An authored CoNLL-U file must declare its language in the filename prefix.");
            Hash128 languageId = LanguageReference.Resolve(languageCode);
            var records = new List<UdIngestRecord>();
            await using var input = new MemoryStream(bytes, writable: false);
            await foreach (UdSentence sentence in UdConlluParser.ParseSentencesAsync(input, ct))
            {
                if (sentence.TextUtf8 is not { Length: > 0 })
                    throw new InvalidDataException("An authored CoNLL-U exemplar must retain its exact # text surface.");
                records.Add(new UdIngestRecord(sentence, languageId, languageCode));
            }
            if (records.Count == 0)
                throw new InvalidDataException("An authored CoNLL-U artifact must contain a complete sentence annotation.");
            witness = new OperationalConlluWitness(records, "operational/" + relativePath,
                canonicalNames ?? new(StringComparer.Ordinal), seenSourceDeclarations ?? new());
        }
        return new GrammarComposeRecord(bytes, modality,
            FileMetadata: GrammarSourceFileSupport.MetadataFromPath(filePath, relativePath, modality),
            StructureWitness: witness);
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
