using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Code;

public sealed class RepoDecomposer : DecomposerOrchestrator
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/RepoDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly Hash128 RepoTypeId = EntityTypeRegistry.RepoRoot;
    private static readonly Hash128 FileTypeId = EntityTypeRegistry.SourceFile;

    private static readonly Dictionary<string, string> FileNameToModality =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CMakeLists.txt"] = "cmake",
        };

    public override Hash128 SourceId => Source;
    public override string SourceName => "RepoDecomposer";
    public override int LayerOrder => 2;
    public override Hash128 TrustClassId => TrustClass;

    private readonly HashSet<string> _canonicalNames = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = await SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["RepoRoot", "SourceFile"],
            relationNodeNames: ["CONTAINS", "CALLS", "DEFINES", "REFERENCES",
                "HAS_EXAMPLE", "HAS_DEFINITION"],
            ct: ct);
        _canonicalNames.UnionWith(boot.CanonicalNames);
    }

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var root = context.EcosystemPath;
        if (!Directory.Exists(root)) yield break;

        string repoCanonical = $"repo:{Path.GetFullPath(root)}/v1";
        _canonicalNames.Add(repoCanonical);
        var repoId = Hash128.OfCanonical(repoCanonical);
        int batch = options.BatchSize > 1 ? options.BatchSize : 512;

        if (!options.DryRun)
        {
            await foreach (var change in RunComposePhaseAsync(
                SingleRepoRootAsync(repoCanonical, repoId, ct), StageRepoRoot,
                "root", SourceTrust.StructuredCorpus, 1, context, options, ct))
                yield return change;
        }

        var files = EnumerateRepoFiles(root).ToList();
        if (files.Count == 0) yield break;

        await foreach (var change in RunGrammarComposePhaseAsync(
            EnumerateRecordsAsync(files, root, repoId, ct),
            SourceTrust.StructuredCorpus, "repo", batch, context, options, ct))
            yield return change;
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(Directory.Exists(context.EcosystemPath)
            ? EnumerateRepoFiles(context.EcosystemPath).Count()
            : null);

    private readonly record struct RepoRootRecord(string Canonical, Hash128 Id);

    private static void StageRepoRoot(RepoRootRecord rec, SubstrateChangeBuilder b)
    {
        b.AddEntity(new EntityRow(rec.Id, EntityTier.Document, RepoTypeId, Source));
        if (TextEntityBuilder.TryDecomposeRoot(Encoding.UTF8.GetBytes(rec.Canonical),
                out _, out _, out double cx, out double cy, out double cz, out double cm))
        {
            Span<double> coord = stackalloc double[4] { cx, cy, cz, cm };
            Hash128 physId = PhysicalityId.Compute(rec.Id, PhysicalityType.Content);
            b.AddPhysicality(new PhysicalityRow(
                Id: physId, EntityId: rec.Id, SourceId: Source,
                Type: PhysicalityType.Content,
                CoordX: cx, CoordY: cy, CoordZ: cz, CoordM: cm,
                HilbertIndex: Hilbert128.Encode(coord),
                TrajectoryXyzm: null, NConstituents: 0,
                AlignmentResidual: null, SourceDim: null, ObservedAtUnixUs: 0));
        }
    }

    private static async IAsyncEnumerable<RepoRootRecord> SingleRepoRootAsync(
        string repoCanonical, Hash128 repoId, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        yield return new RepoRootRecord(repoCanonical, repoId);
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<GrammarComposeRecord> EnumerateRecordsAsync(
        IReadOnlyList<(string File, string Modality)> files,
        string root,
        Hash128 repoId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var (file, modality) in files)
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, ct); }
            catch (IOException) { continue; }
            if (bytes.Length == 0) continue;

            string relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
            var filename = Path.GetFileNameWithoutExtension(file);
            var segments = new List<string>();
            if (!string.IsNullOrEmpty(filename))
            {
                foreach (var seg in filename.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (seg.Length >= 3)
                        segments.Add(seg.ToLowerInvariant());
                }
            }

            yield return new GrammarComposeRecord(
                bytes, modality,
                ExampleSegments: segments.Count > 0 ? segments : null,
                ConceptAnchorKey: relPath,
                ConceptCategoryTypeId: FileTypeId,
                ParentContainerId: repoId);
        }
    }

    private static IEnumerable<(string File, string Modality)> EnumerateRepoFiles(string root)
    {
        char sep = Path.DirectorySeparatorChar;
        string[] skipSegs = { $"{sep}obj{sep}", $"{sep}bin{sep}", $"{sep}.git{sep}", $"{sep}node_modules{sep}" };

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                                      .OrderBy(p => p, StringComparer.Ordinal))
        {
            if (skipSegs.Any(s => file.Contains(s))) continue;

            string fileName = Path.GetFileName(file);
            string ext = Path.GetExtension(file);
            if (ext.Length > 0 && ext[0] == '.') ext = ext[1..];
            string? modality = FileNameToModality.TryGetValue(fileName, out var nameMod)
                ? nameMod
                : (ext.Length == 0 ? null : GrammarDecomposer.ModalityByExt(ext.ToLowerInvariant()));
            if (modality is null) continue;

            if (GrammarDecomposer.LookupById(modality) == IntPtr.Zero) continue;
            yield return (file, modality);
        }
    }
}
