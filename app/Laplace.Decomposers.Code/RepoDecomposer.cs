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

    private static readonly Dictionary<string, string> ExtToModality =
        new(StringComparer.OrdinalIgnoreCase)
        {
            
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
            
            ["java"]    = "java",
            ["rb"]      = "ruby",   ["rake"] = "ruby",
            ["jl"]      = "julia",
            ["kt"]      = "kotlin", ["kts"]  = "kotlin",
            ["php"]     = "php",
            ["sql"]     = "sql",    ["ddl"]  = "sql",   ["dml"] = "sql",
            ["swift"]   = "swift",
            
            ["cu"]      = "cuda",   ["cuh"]  = "cuda",
            ["glsl"]    = "glsl",   ["vert"] = "glsl",  ["frag"] = "glsl",
                                    ["comp"] = "glsl",  ["geom"] = "glsl",
            ["hlsl"]    = "hlsl",   ["hlsli"] = "hlsl",
            ["wgsl"]    = "wgsl",
            ["f90"]     = "fortran", ["f95"] = "fortran", ["f"]  = "fortran",
                                     ["for"] = "fortran",
            ["s"]       = "asm",    
            ["ll"]      = "llvm",
            ["mlir"]    = "mlir",
            ["cmake"]   = "cmake",
            ["ispc"]    = "ispc",
            ["zig"]     = "zig",
        };

    
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
        _canonicalNames.UnionWith(boot.CanonicalNames);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var root = context.EcosystemPath;
        if (!Directory.Exists(root)) yield break;

        
        string repoCanonical = $"repo:{Path.GetFullPath(root)}/v1";
        _canonicalNames.Add(repoCanonical);
        var repoId = Hash128.OfCanonical(repoCanonical);
        int batch = options.BatchSize > 1 ? options.BatchSize : 32;
        var b = NewBuilder(0);
        int inBatch = 0, bn = 0;
        b.AddEntity(new EntityRow(repoId, EntityTier.Vocabulary, RepoTypeId, Source));

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
                _canonicalNames.UnionWith(geb.NodeTypeCanonicalNames);
            }
            catch { continue; }

            if (codeRootId == default) continue;

            foreach (var e in ents) b.AddEntity(e);
            foreach (var p in phys) b.AddPhysicality(p);
            foreach (var a in atts) b.AddAttestation(a);

            
            string relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
            string fileCanonical = $"source/file/{relPath}/v1";
            _canonicalNames.Add(fileCanonical);
            var filePathId = Hash128.OfCanonical(fileCanonical);
            b.AddEntity(new EntityRow(filePathId, EntityTier.Vocabulary, FileTypeId, Source));
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
