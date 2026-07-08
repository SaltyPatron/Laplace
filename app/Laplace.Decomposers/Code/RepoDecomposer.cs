using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Code;

public sealed class RepoDecomposer : GrammarComposeDecomposer
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
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "repo";

    private readonly HashSet<string> _canonicalNames = new(StringComparer.Ordinal);
    private Hash128 _repoId;

    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = await SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["RepoRoot", "SourceFile"],
            relationNodeNames: ["CONTAINS", "CALLS", "DEFINES", "REFERENCES",
                "HAS_EXAMPLE", "HAS_DEFINITION"],
            ct: ct);
        _canonicalNames.UnionWith(boot.CanonicalNames);

        var root = context.EcosystemPath;
        if (!Directory.Exists(root)) return;

        string repoCanonical = $"repo:{Path.GetFullPath(root)}/v1";
        _canonicalNames.Add(repoCanonical);
        _repoId = Hash128.OfCanonical(repoCanonical);

        var seed = new SubstrateChangeBuilder(Source, "bootstrap/repo-root", null,
            entityCapacity: 1, physicalityCapacity: 1, attestationCapacity: 0);
        StageRepoRoot(seed, repoCanonical, _repoId);
        await context.Writer.ApplyAsync(seed.Build(), ct);
    }

    protected override async IAsyncEnumerable<GrammarComposeRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_repoId == default)
        {
            if (!Directory.Exists(ecosystemPath)) yield break;
            string repoCanonical = $"repo:{Path.GetFullPath(ecosystemPath)}/v1";
            _repoId = Hash128.OfCanonical(repoCanonical);
        }

        var files = EnumerateRepoFiles(ecosystemPath).ToList();
        foreach (var (file, modality) in files)
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, ct); }
            catch (IOException ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "RepoDecomposer: failed to read '{File}': {Message}", file, ex.Message);
                continue;
            }
            if (bytes.Length == 0) continue;

            string relPath = Path.GetRelativePath(ecosystemPath, file).Replace('\\', '/');
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
                ParentContainerId: _repoId);
        }
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(Directory.Exists(context.EcosystemPath)
            ? EnumerateRepoFiles(context.EcosystemPath).Count()
            : null);

    private static void StageRepoRoot(SubstrateChangeBuilder b, string repoCanonical, Hash128 repoId)
    {
        b.AddEntity(new EntityRow(repoId, EntityTier.Document, RepoTypeId, Source));
        if (TextEntityBuilder.TryDecomposeRoot(Encoding.UTF8.GetBytes(repoCanonical),
                out _, out _, out double cx, out double cy, out double cz, out double cm))
        {
            Span<double> coord = stackalloc double[4] { cx, cy, cz, cm };
            Hash128 physId = PhysicalityId.Compute(repoId, PhysicalityType.Content);
            b.AddPhysicality(new PhysicalityRow(
                Id: physId, EntityId: repoId, SourceId: Source,
                Type: PhysicalityType.Content,
                CoordX: cx, CoordY: cy, CoordZ: cz, CoordM: cm,
                HilbertIndex: Hilbert128.Encode(coord),
                TrajectoryXyzm: null, NConstituents: 0,
                AlignmentResidual: null, SourceDim: null, ObservedAtUnixUs: 0));
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
