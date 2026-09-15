using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Code;

public class RepoDecomposer : GrammarComposeDecomposerMultiFile<RepoSource, FullScope>,
    IIngestInventoryProvider, IIngestArtifactGraphProvider
{
    private readonly VerifiedGitRepository? verifiedRepository;
    public RepoDecomposer() { }
    public RepoDecomposer(VerifiedGitRepository repository) => verifiedRepository = repository;

    public static readonly Hash128 Source = RepoSource.SourceId;
    public static readonly Hash128 TrustClass = RepoSource.TrustClass;

    private static readonly Hash128 RepoTypeId = EntityTypeRegistry.RepoRoot;
    private static readonly Hash128 FileTypeId = EntityTypeRegistry.SourceFile;

    private static readonly Dictionary<string, string> FileNameToModality =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CMakeLists.txt"] = "cmake",
        };

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "repo";

    private readonly ConcurrentDictionary<string, byte> _canonicalNames = new(StringComparer.Ordinal);
    private Hash128 _repoId;
    public VerifiedGitRepository? VerifiedRepository => verifiedRepository;
    public Hash128? ProvenanceRoot { get; private set; }
    public Hash128 RepositoryId => _repoId;

    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames.Keys.ToArray();
    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => _canonicalNames;

    protected override async Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct)
    {
        var root = context.EcosystemPath;
        if (!Directory.Exists(root)) return;

        if (verifiedRepository is null) ThrowIfNestedRepos(root);

        string repoCanonical = $"repo:{Path.GetFullPath(root)}/v1";
        _canonicalNames.TryAdd(repoCanonical, 0);
        _repoId = Hash128.OfCanonical(repoCanonical);

        var seed = new SubstrateChangeBuilder(Source, "bootstrap/repo-root", null,
            entityCapacity: 16, physicalityCapacity: 16, attestationCapacity: 0)
            .DeclareSourcePrior(SourceTrust);
        StageRepoRoot(seed, repoCanonical, _repoId);
        if (verifiedRepository is null) await context.Writer.ApplyAsync(seed.Build(), ct);
        else
        {
            verifiedRepository.VerifyUnchanged();
            ProvenanceRoot = ContentEmitter.Emit(seed, Encoding.UTF8.GetString(verifiedRepository.ProvenanceUtf8), Source)
                ?? throw new InvalidDataException("Git provenance did not produce native content.");
            seed.AddAttestation(NativeAttestation.CategoricalResolved(
                _repoId, RepoSource.ReferencesTypeId, ProvenanceRoot.Value, Source, null, SourceTrust));
            await context.Writer.ApplyWorkingSetAsync([seed.Build()], token =>
            {
                token.ThrowIfCancellationRequested();
                verifiedRepository.VerifyUnchanged();
                return ValueTask.CompletedTask;
            }, ct);
        }
    }

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        if (_repoId == default && Directory.Exists(ecosystemPath))
            _repoId = Hash128.OfCanonical($"repo:{Path.GetFullPath(ecosystemPath)}/v1");
        if (verifiedRepository is not null)
            return verifiedRepository.Graph.Selected.Select(a => (a.Path, a.FileLabel)).ToList();
        return EnumerateRepoFiles(ecosystemPath)
            .Select(x => (
                x.File,
                $"repo/{Path.GetRelativePath(ecosystemPath, x.File).Replace('\\', '/')}"))
            .ToList();
    }

    protected override async IAsyncEnumerable<GrammarComposeRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? modality = verifiedRepository is null ? ModalityFor(filePath)
            : verifiedRepository.Entries.Single(e => Path.Combine(verifiedRepository.Root, e.Path) == filePath).Modality;
        if (modality is null) yield break;
        byte[] bytes;
        try { bytes = verifiedRepository is null ? await File.ReadAllBytesAsync(filePath, ct)
            : verifiedRepository.ReadVerified(verifiedRepository.Entries.Single(e => Path.Combine(verifiedRepository.Root, e.Path) == filePath)); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"RepoDecomposer: failed to read '{filePath}': {ex.Message}", ex);
        }
        if (bytes.Length == 0)
            throw new InvalidDataException(
                $"RepoDecomposer: admitted source file '{filePath}' is empty");

        string relPath = verifiedRepository is not null ? Path.GetRelativePath(verifiedRepository.Root, filePath).Replace('\\', '/')
            : fileLabel.StartsWith("repo/", StringComparison.Ordinal)
            ? fileLabel["repo/".Length..]
            : Path.GetFileName(filePath);
        var filename = Path.GetFileNameWithoutExtension(filePath);
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
            ParentContainerId: _repoId,
            FileMetadata: GrammarSourceFileSupport.MetadataFromPath(
                filePath, relPath, modality),
            RequireSourceAst: verifiedRepository is not null,
            RawText: verifiedRepository is not null && modality == "text");
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(Directory.Exists(context.EcosystemPath)
            ? EnumerateRepoFiles(context.EcosystemPath).Count()
            : null);

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        if (!Directory.Exists(context.EcosystemPath) && !context.HasArtifactGraph)
            return Task.FromResult<IngestInventory?>(null);
        var files = context.HasArtifactGraph
            ? context.SelectedArtifacts.Select(static artifact =>
                    new IngestFileSpec(artifact.FileLabel, artifact.Path, 1))
                .ToList()
            : EnumerateRepoFiles(context.EcosystemPath)
                .Select(static file => new IngestFileSpec(
                    Path.GetFileName(file.File), file.File, 1))
                .ToList();
        long total = options.MaxInputUnits > 0
            ? Math.Min(files.Count, options.MaxInputUnits)
            : files.Count;
        return Task.FromResult<IngestInventory?>(
            new IngestInventory("repository files", total, files, TracksFileCompletion: true));
    }

    public Task<IngestArtifactGraph?> DescribeArtifactsAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (verifiedRepository is not null) return Task.FromResult<IngestArtifactGraph?>(verifiedRepository.Graph);
        return Task.FromResult(GrammarSourceFileSupport.BuildArtifactGraph(
            ecosystemPath, RepoSource.SourceName, "repo", ModalityFor));
    }

    private static void StageRepoRoot(SubstrateChangeBuilder b, string repoCanonical, Hash128 repoId)
    {
        b.AddEntity(new EntityRow(repoId, EntityTier.Document, RepoTypeId, Source));

        // repoId is a stable governed handle for this repository. The canonical path text is
        // actual content and therefore enters through the normal text Merkle DAG all the way
        // to Unicode/codepoints. The handle's spatial placement is a Projection onto that
        // content root; treating the handle itself as atomic Content forged a fake leaf.
        Hash128 pathRoot = ContentEmitter.Emit(b, repoCanonical, Source)
            ?? throw new InvalidOperationException("repository canonical path did not produce content");
        if (!TextEntityBuilder.TryDecomposeRoot(Encoding.UTF8.GetBytes(repoCanonical),
                out var decomposedRoot, out _, out double cx, out double cy, out double cz, out double cm)
            || decomposedRoot != pathRoot)
            throw new InvalidOperationException("repository canonical path content root was not reproducible");

        Span<double> coord = stackalloc double[4] { cx, cy, cz, cm };
        Hash128 physId = PhysicalityId.Compute(repoId, PhysicalityType.Projection);
        b.AddPhysicality(new PhysicalityRow(
            Id: physId, EntityId: repoId, SourceId: Source,
            Type: PhysicalityType.Projection,
            CoordX: cx, CoordY: cy, CoordZ: cz, CoordM: cm,
            HilbertIndex: Hilbert128.Encode(coord),
            TrajectoryXyzm: Trajectory.Build([pathRoot]), NConstituents: 1,
            AlignmentResidual: null, SourceDim: null, ObservedAtUnixUs: 0));
    }

    internal static void ThrowIfNestedRepos(string root)
    {
        var nested = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(root, ".git", SearchOption.AllDirectories))
        {
            if (string.Equals(
                    Path.GetFullPath(Path.GetDirectoryName(dir) ?? ""),
                    Path.GetFullPath(root),
                    StringComparison.Ordinal))
                continue;
            nested.Add(dir);
        }
        if (nested.Count == 0) return;

        throw new InvalidOperationException(
            $"RepoDecomposer: '{root}' contains {nested.Count} nested git repositor{(nested.Count == 1 ? "y" : "ies")} " +
            $"beyond its own root ({string.Join(", ", nested.Take(5).Select(Path.GetDirectoryName))}" +
            $"{(nested.Count > 5 ? ", ..." : "")}) — ingesting this path would flatten multiple independent " +
            "repos into one repo-root identity. Run 'ingest repo <path>' once per discovered repo root instead.");
    }

    private static IEnumerable<(string File, string Modality)> EnumerateRepoFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                                      .OrderBy(p => p, StringComparer.Ordinal))
        {
            if (VendoredPathFilter.IsVendoredOrBuildPath(file, root)) continue;

            string? modality = ModalityFor(file);
            if (modality is null) continue;

            if (GrammarDecomposer.LookupById(modality) == IntPtr.Zero) continue;
            yield return (file, modality);
        }
    }

    public static string? VerifiedModalityFor(string file, string? requiredModality = null)
    {
        // A declared C++ repository gives .h its C++ translation-unit context.
        // Loose-file ingestion keeps its existing extension classification.
        string? modality = requiredModality == "cpp" && Path.GetExtension(file) == ".h"
            ? "cpp" : ModalityFor(file);
        return modality is not null && GrammarDecomposer.LookupById(modality) != IntPtr.Zero ? modality : null;
    }

    internal static string? ModalityFor(string file)
    {
        string fileName = Path.GetFileName(file);
        string ext = Path.GetExtension(file);
        if (ext.Length > 0 && ext[0] == '.') ext = ext[1..];
        return FileNameToModality.TryGetValue(fileName, out var nameMod)
            ? nameMod
            : (ext.Length == 0 ? null : GrammarDecomposer.ModalityByExt(ext.ToLowerInvariant()));
    }
}
