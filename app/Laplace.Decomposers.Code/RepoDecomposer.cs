using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Code;

/// <summary>
/// Deposits a source repository as structured code testimony, layered on top of CodeDecomposer.
/// Beyond raw AST parsing, it adds:
///   - A repo-root entity + CONTAINS links to each top-level directory/file
///   - A file-path entity per file (content-addressed via relative path text) + CONTAINS from parent
///   - HAS_EXAMPLE from the file-path entity to the parsed code root entity
///   - CALLS/DEFINES/REFERENCES arcs from tree-sitter tags.scm (same as CodeDecomposer)
/// This lets the substrate answer structural questions: "what files does Laplace have",
/// "what does bilinear_edges.cpp contain", etc., and is the primary ingest path for this repo.
/// </summary>
public sealed class RepoDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/RepoDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly Hash128 RepoTypeId =
        Hash128.OfCanonical("substrate/type/RepoRoot/v1");
    private static readonly Hash128 FileTypeId =
        Hash128.OfCanonical("substrate/type/SourceFile/v1");

    private static readonly Dictionary<string, string> ExtToModality =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // core
            ["py"]   = "python",
            ["c"]    = "c",    ["h"]   = "c",
            ["cpp"]  = "cpp",  ["cc"]  = "cpp", ["cxx"] = "cpp", ["hpp"] = "cpp", ["hh"] = "cpp",
            ["js"]   = "javascript", ["mjs"] = "javascript", ["cjs"] = "javascript",
            ["ts"]   = "typescript", ["tsx"] = "typescript",
            ["rs"]   = "rust",
            ["go"]   = "go",
            ["cs"]   = "c-sharp",
            ["sh"]   = "bash", ["bash"] = "bash",
            ["json"] = "json",
            ["md"]   = "markdown",
            // language grammars
            ["java"]    = "java",
            ["rb"]      = "ruby",   ["rake"] = "ruby",
            ["jl"]      = "julia",
            ["kt"]      = "kotlin", ["kts"]  = "kotlin",
            ["php"]     = "php",
            // HPC/compute — enabled once grammars compiled
            ["cu"]      = "cuda",   ["cuh"]  = "cuda",
            ["glsl"]    = "glsl",   ["vert"] = "glsl",  ["frag"] = "glsl",
                                    ["comp"] = "glsl",  ["geom"] = "glsl",
            ["hlsl"]    = "hlsl",   ["hlsli"] = "hlsl",
            ["wgsl"]    = "wgsl",
            ["f90"]     = "fortran", ["f95"] = "fortran", ["f"]  = "fortran",
                                     ["for"] = "fortran",
            ["s"]       = "asm",    // GAS assembly
            ["ll"]      = "llvm",
            ["mlir"]    = "mlir",
            ["cmake"]   = "cmake",
            ["ispc"]    = "ispc",
            ["zig"]     = "zig",
        };

    // Files whose modality is keyed on name rather than extension.
    private static readonly Dictionary<string, string> FileNameToModality =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CMakeLists.txt"] = "cmake",
        };

    public Hash128 SourceId     => Source;
    public string  SourceName   => "RepoDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("RepoRoot");
        boot.AddType("SourceFile");
        boot.AddRelationType("CONTAINS");
        boot.AddRelationType("CALLS");
        boot.AddRelationType("DEFINES");
        boot.AddRelationType("REFERENCES");
        boot.AddRelationType("HAS_EXAMPLE");
        boot.AddRelationType("HAS_DEFINITION");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var root = context.EcosystemPath;
        if (!Directory.Exists(root)) yield break;

        // Canonical root entity keyed on the repo path
        var repoId = Hash128.OfCanonical($"repo:{Path.GetFullPath(root)}/v1");
        int batch = options.BatchSize > 1 ? options.BatchSize : 32;
        var b = NewBuilder(0);
        int inBatch = 0, bn = 0;
        b.AddEntity(new EntityRow(repoId, (byte)MetaTier.Meta, RepoTypeId, Source));

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
            if (!FileNameToModality.TryGetValue(fileName, out var modality) &&
                !ExtToModality.TryGetValue(ext, out modality)) continue;

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
                (ents, phys, atts, codeRootId) = geb.Build(SourceTrust.StructuredCorpus);
            }
            catch { continue; }

            if (codeRootId == default) continue;

            foreach (var e in ents) b.AddEntity(e);
            foreach (var p in phys) b.AddPhysicality(p);
            foreach (var a in atts) b.AddAttestation(a);

            // File-path entity: relative path content-addressed so it reconciles with text search.
            string relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
            var filePathId = ContentEmitter.Emit(b, relPath, Source);
            if (filePathId.HasValue)
            {
                b.AddEntity(new EntityRow(filePathId.Value, (byte)MetaTier.Meta, FileTypeId, Source));
                b.AddAttestation(RelationTypeRegistry.Attest(
                    repoId,            "CONTAINS",     filePathId.Value, Source, SourceTrust.StructuredCorpus));
                b.AddAttestation(RelationTypeRegistry.Attest(
                    filePathId.Value,  "HAS_EXAMPLE",  codeRootId,       Source, SourceTrust.StructuredCorpus));
                b.AddAttestation(RelationTypeRegistry.Attest(
                    codeRootId, "HAS_DEFINITION", filePathId.Value,      Source, SourceTrust.StructuredCorpus));
            }

            // Also keyword-link the filename itself (e.g. "bilinear_edges") to the code root
            var filename = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrEmpty(filename))
            {
                // Split on underscores, hyphens, dots — each segment links to the code
                foreach (var seg in filename.Split(new char[] { '_', '-', '.' },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    if (seg.Length < 3) continue;
                    var segId = ContentEmitter.Emit(b, seg.ToLowerInvariant(), Source);
                    if (segId.HasValue)
                        b.AddAttestation(RelationTypeRegistry.Attest(
                            segId.Value, "HAS_EXAMPLE", codeRootId, Source, SourceTrust.StructuredCorpus));
                }
            }

            if (++inBatch >= batch)
            {
                if (!options.DryRun) yield return b.Build();
                b = NewBuilder(++bn);
                inBatch = 0;
                await Task.Yield();
            }
        }

        if (inBatch > 0 && !options.DryRun) yield return b.Build();
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static SubstrateChangeBuilder NewBuilder(int n) =>
        new(Source, $"repo/{n}", null,
            entityCapacity: 4096, physicalityCapacity: 4096, attestationCapacity: 4096);
}
