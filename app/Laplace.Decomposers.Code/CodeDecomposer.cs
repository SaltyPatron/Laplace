using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Code;

/// <summary>
/// Witness adapter that ingests source files as structured content: each file is parsed by its
/// grammar (the shared <see cref="GrammarDecomposer"/> mechanism) and composed into the substrate
/// by <see cref="GrammarEntityBuilder"/> — entities/physicalities for the AST + PRECEDES over
/// siblings. Domain-specific, modality-agnostic underneath; chess/DNA/etc. are sibling adapters.
/// </summary>
public sealed class CodeDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/CodeDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    // file extension (no dot) -> modality id; mirrors the engine grammar_registry ext table.
    private static readonly Dictionary<string, string> ExtToModality =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["py"] = "python",
            ["c"] = "c", ["h"] = "c",
            ["cpp"] = "cpp", ["cc"] = "cpp", ["cxx"] = "cpp", ["hpp"] = "cpp", ["hh"] = "cpp",
            ["js"] = "javascript", ["mjs"] = "javascript", ["cjs"] = "javascript",
            ["rs"] = "rust",
            ["go"] = "go",
            ["cs"] = "c-sharp",
            ["sh"] = "bash", ["bash"] = "bash",
            ["json"] = "json",
            ["md"] = "markdown", ["markdown"] = "markdown",
        };

    public Hash128 SourceId     => Source;
    public string  SourceName   => "CodeDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        // Structure rides on PRECEDES (canonical); typed semantic arcs come from tags.scm.
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddRelationType("CALLS");
        boot.AddRelationType("DEFINES");
        boot.AddRelationType("REFERENCES");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var files = EnumerateCodeFiles(context.EcosystemPath).ToList();
        if (files.Count == 0) yield break;
        int batch = options.BatchSize > 1 ? options.BatchSize : 64;

        var b = NewBuilder(0);
        int inBatch = 0, bn = 0;

        foreach (var (file, modality) in files)
        {
            ct.ThrowIfCancellationRequested();

            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, ct); }
            catch { continue; }
            if (bytes.Length == 0) continue;

            IntPtr recipe = GrammarDecomposer.LookupById(modality);
            if (recipe == IntPtr.Zero) continue;

            ImmutableArray<EntityRow> ents;
            ImmutableArray<PhysicalityRow> phys;
            ImmutableArray<AttestationRow> atts;
            try
            {
                using var ast = GrammarDecomposer.Parse(bytes, recipe);
                var geb = new GrammarEntityBuilder(
                    bytes, ast, Source, modality, recipe, GrammarTags.TagsSource(modality));
                (ents, phys, atts, _) = geb.Build(SourceTrust.StructuredCorpus);
            }
            catch
            {
                continue;  // a single unparseable/degenerate file must not abort the run
            }

            foreach (var e in ents) b.AddEntity(e);
            foreach (var p in phys) b.AddPhysicality(p);
            foreach (var a in atts) b.AddAttestation(a);

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
        => Task.FromResult<long?>(EnumerateCodeFiles(context.EcosystemPath).Count());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IEnumerable<(string File, string Modality)> EnumerateCodeFiles(string root)
    {
        if (File.Exists(root))
        {
            var m = ModalityOf(root);
            if (m is not null) yield return (root, m);
            yield break;
        }
        if (!Directory.Exists(root)) yield break;
        char sep = Path.DirectorySeparatorChar;
        string objSeg = $"{sep}obj{sep}", binSeg = $"{sep}bin{sep}", gitSeg = $"{sep}.git{sep}";
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                                      .OrderBy(p => p, StringComparer.Ordinal))
        {
            if (file.Contains(objSeg) || file.Contains(binSeg) || file.Contains(gitSeg)) continue;
            var m = ModalityOf(file);
            if (m is not null) yield return (file, m);
        }
    }

    private static string? ModalityOf(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Length > 0 && ext[0] == '.') ext = ext[1..];
        return ExtToModality.TryGetValue(ext, out var m) ? m : null;
    }

    private static SubstrateChangeBuilder NewBuilder(int n) =>
        new(Source, $"code/{n}", null,
            entityCapacity: 4096, physicalityCapacity: 4096, attestationCapacity: 2048);
}
