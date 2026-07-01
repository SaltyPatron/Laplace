using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Code;











public sealed class RepoDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/RepoDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly Hash128 RepoTypeId = EntityTypeRegistry.RepoRoot;
    private static readonly Hash128 FileTypeId = EntityTypeRegistry.SourceFile;

    // ext->modality is owned by the native registry (GrammarDecomposer.ModalityByExt); this map only
    // covers extension-less filenames the ext lookup can't reach.
    private static readonly Dictionary<string, string> FileNameToModality =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CMakeLists.txt"] = "cmake",
        };

    public Hash128 SourceId     => Source;
    public string  SourceName   => "RepoDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    
    
    
    
    private readonly HashSet<string> _canonicalNames = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = await SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["RepoRoot", "SourceFile"],
            relationNodeNames: ["CONTAINS", "CALLS", "DEFINES", "REFERENCES",
                "HAS_EXAMPLE", "HAS_DEFINITION"],
            ct: ct);
        _canonicalNames.UnionWith(boot.CanonicalNames);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var root = context.EcosystemPath;
        if (!Directory.Exists(root)) yield break;

        // LOCAL PROVENANCE ANCHOR, not content: the repo root is identified by its absolute on-disk
        // path, which is machine-specific and must NOT converge across machines (two checkouts of the
        // same repo on different hosts are different roots). So it stays a geometry-free Vocabulary
        // anchor (Hash128.OfCanonical), unlike the relative file paths below which ARE content.
        string repoCanonical = $"repo:{Path.GetFullPath(root)}/v1";
        _canonicalNames.Add(repoCanonical);
        var repoId = Hash128.OfCanonical(repoCanonical);
        int batch = options.BatchSize > 1 ? options.BatchSize : 512;
        var reader = context.Reader;
        var b = NewBuilder(0, reader);
        int inBatch = 0, bn = 0;
        b.AddEntity(new EntityRow(repoId, EntityTier.Document, RepoTypeId, Source));

        char sep = Path.DirectorySeparatorChar;
        string[] skipSegs = { $"{sep}obj{sep}", $"{sep}bin{sep}", $"{sep}.git{sep}", $"{sep}node_modules{sep}" };

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                                      .OrderBy(p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (skipSegs.Any(s => file.Contains(s))) continue;

            string fileName = Path.GetFileName(file);
            string ext = Path.GetExtension(file);
            if (ext.Length > 0 && ext[0] == '.') ext = ext[1..];
            string? modality = FileNameToModality.TryGetValue(fileName, out var nameMod)
                ? nameMod
                : (ext.Length == 0 ? null : GrammarDecomposer.ModalityByExt(ext.ToLowerInvariant()));
            if (modality is null) continue;

            IntPtr recipe = GrammarDecomposer.LookupById(modality);
            if (recipe == IntPtr.Zero) continue;

            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, ct); }
            catch { continue; }
            if (bytes.Length == 0) continue;

            ImmutableArray<EntityRow>      ents;
            ImmutableArray<PhysicalityRow> phys;
            ImmutableArray<AttestationRow> atts;
            Hash128 codeRootId;
            try
            {
                using var ast = GrammarDecomposer.Parse(bytes, recipe);
                var geb = new GrammarEntityBuilder(
                    bytes, ast, Source, modality, recipe, GrammarTags.TagsSource(modality));
                (ents, phys, atts, codeRootId) = await geb.BuildAsync(
                    SourceTrust.StructuredCorpus, context.Reader, ct);
                _canonicalNames.UnionWith(geb.NodeTypeCanonicalNames);
            }
            catch { continue; }

            if (codeRootId == default) continue;

            foreach (var e in ents) b.AddEntity(e);
            foreach (var p in phys) b.AddPhysicality(p);
            foreach (var a in atts) b.AddAttestation(a);

            // The relative file path IS content (a meaningful surface string that converges across
            // checkouts): emit it via the shared CategoryAnchor (ContentEmitter + IS_TYPED_AS
            // SourceFile), exactly like CILI treats a synset key, instead of baking it into a nameless
            // Hash128.OfCanonical("source/file/{relPath}/v1") vocabulary row with no geometry.
            string relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
            var filePathAnchor = CategoryAnchor.Emit(b, relPath, FileTypeId, Source, SourceTrust.StructuredCorpus);
            if (filePathAnchor is null) continue;
            Hash128 filePathId = filePathAnchor.Value;
            b.AddAttestation(NativeAttestation.Categorical(
                repoId,            "CONTAINS",     filePathId, Source, SourceTrust.StructuredCorpus));
            b.AddAttestation(NativeAttestation.Categorical(
                filePathId,        "HAS_EXAMPLE",  codeRootId, Source, SourceTrust.StructuredCorpus));
            b.AddAttestation(NativeAttestation.Categorical(
                codeRootId, "HAS_DEFINITION", filePathId,      Source, SourceTrust.StructuredCorpus));

            
            var filename = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrEmpty(filename))
            {
                
                foreach (var seg in filename.Split(new char[] { '_', '-', '.' },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    if (seg.Length < 3) continue;
                    var segId = ContentEmitter.Emit(b, seg.ToLowerInvariant(), Source);
                    if (segId.HasValue)
                        b.AddAttestation(NativeAttestation.Categorical(
                            segId.Value, "HAS_EXAMPLE", codeRootId, Source, SourceTrust.StructuredCorpus));
                }
            }

            if (++inBatch >= batch)
            {
                if (!options.DryRun) { yield return await b.SetInputUnitsConsumed(inBatch).BuildAsync(ct); IntentStage.ResetContentBank(); }
                b = NewBuilder(++bn, reader);
                inBatch = 0;
            }
        }

        if (inBatch > 0 && !options.DryRun)
        {
            yield return await b.SetInputUnitsConsumed(inBatch).BuildAsync(ct);
            IntentStage.ResetContentBank();
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Relative file paths and filename keyword segments route through the SHARED two-phase
    // containment (EnableDeferredContent); the parsed-code grammar tree is already containment-deduped
    // inside GrammarEntityBuilder.BuildAsync. Drain via BuildAsync so the deferred probe runs.
    private static SubstrateChangeBuilder NewBuilder(int n, ISubstrateReader? reader) =>
        new SubstrateChangeBuilder(Source, $"repo/{n}", null,
            entityCapacity: 4096, physicalityCapacity: 4096, attestationCapacity: 4096);
}
