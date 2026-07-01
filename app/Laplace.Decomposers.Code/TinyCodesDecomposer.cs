using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Parquet;
using Parquet.Schema;

namespace Laplace.Decomposers.Code;








public sealed class TinyCodesDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/TinyCodesDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly Hash128 CodeConceptTypeId = EntityTypeRegistry.CodeConcept;

    
    
    private static readonly Dictionary<string, string?> LangModality =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["python"]     = "python",
            ["javascript"] = "javascript",
            ["typescript"] = "typescript",
            ["ruby"]       = "ruby",
            ["julia"]      = "julia",
            ["rust"]       = "rust",
            ["c++"]        = "cpp",
            ["bash"]       = "bash",
            ["java"]       = "java",
            ["c#"]         = "c-sharp",
            ["go"]         = "go",
            ["sql"]        = "sql",
            ["cypher"]     = null,
        };

    public Hash128 SourceId     => Source;
    public string  SourceName   => "TinyCodesDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    private readonly HashSet<string> _canonicalNames = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = await SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["CodeConcept"],
            relationNodeNames: ["HAS_EXAMPLE", "HAS_DEFINITION", "CALLS", "DEFINES", "REFERENCES"],
            ct: ct);
        _canonicalNames.UnionWith(boot.CanonicalNames);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var files = EnumerateParquet(context.EcosystemPath).ToList();
        if (files.Count == 0)
        {
            if (Directory.Exists(context.EcosystemPath))
                throw new InvalidOperationException(
                    $"TinyCodesDecomposer: no *.parquet files under '{context.EcosystemPath}' "
                    + "(expected top-level shards from download-code-data.cmd)");
            yield break;
        }

        int batch = options.BatchSize > 1 ? options.BatchSize : 512;
        var reader = context.Reader;
        var b = NewBuilder(0, reader);
        int inBatch = 0, bn = 0;

        foreach (var file in files)
        {
            await foreach (var (conceptKey, lang, prompt, response) in ReadRowsAsync(file, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(response)) continue;

                string? modality = ResolveModality(lang);
                if (modality is null) continue;

                IntPtr recipe = GrammarDecomposer.LookupById(modality);
                if (recipe == IntPtr.Zero) continue;

                byte[] codeBytes;
                try { codeBytes = Encoding.UTF8.GetBytes(response); }
                catch { continue; }
                if (codeBytes.Length == 0) continue;

                ImmutableArray<EntityRow>      ents;
                ImmutableArray<PhysicalityRow> phys;
                ImmutableArray<AttestationRow> atts;
                Hash128 codeRootId;
                try
                {
                    using var ast = GrammarDecomposer.Parse(codeBytes, recipe);
                    var geb = new GrammarEntityBuilder(
                        codeBytes, ast, Source, modality, recipe, GrammarTags.TagsSource(modality));
                    (ents, phys, atts, codeRootId) = await geb.BuildAsync(
                        SourceTrust.StructuredCorpus, context.Reader, ct);
                    _canonicalNames.UnionWith(geb.NodeTypeCanonicalNames);
                }
                catch { continue; }

                if (codeRootId == default) continue;

                foreach (var e in ents) b.AddEntity(e);
                foreach (var p in phys) b.AddPhysicality(p);
                foreach (var a in atts) b.AddAttestation(a);

                
                // The concept/task key is CONTENT, not a synthetic nameless id: emit its surface via the
                // shared CategoryAnchor (ContentEmitter + IS_TYPED_AS CodeConcept), exactly like CILI's
                // HAS_SYNSET_KEY treats a synset key as content. This gives the key a Merkle DAG / tiers
                // / geometry and converges identical task keys across shards, instead of baking the
                // string into a bare Hash128.OfCanonical("tiny-codes/concept/{key}/v1") vocabulary row.
                if (!string.IsNullOrEmpty(conceptKey)
                    && CategoryAnchor.Emit(b, conceptKey, CodeConceptTypeId, Source, SourceTrust.StructuredCorpus) is { } conceptId)
                {
                    b.AddAttestation(NativeAttestation.Categorical(
                        conceptId,  "HAS_EXAMPLE",   codeRootId, Source, SourceTrust.StructuredCorpus));
                    b.AddAttestation(NativeAttestation.Categorical(
                        codeRootId, "HAS_DEFINITION", conceptId,  Source, SourceTrust.StructuredCorpus));
                }

                
                
                
                
                
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    foreach (var kw in ExtractKeywords(prompt))
                    {
                        var wordId = ContentEmitter.Emit(b, kw, Source);
                        if (wordId.HasValue)
                            b.AddAttestation(NativeAttestation.Categorical(
                                wordId.Value, "HAS_EXAMPLE", codeRootId, Source, SourceTrust.StructuredCorpus));
                    }
                }

                if (++inBatch >= batch)
                {
                    if (!options.DryRun) { yield return await b.SetInputUnitsConsumed(inBatch).BuildAsync(ct); IntentStage.ResetContentBank(); }
                    b = NewBuilder(++bn, reader);
                    inBatch = 0;
                }
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

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "that", "this", "with", "from", "have", "will", "been", "they", "what",
        "when", "which", "your", "into", "more", "some", "than", "then", "also",
        "does", "each", "just", "here", "make", "only", "like", "over", "even",
        "should", "could", "would", "using", "given", "takes", "returns", "given",
        "write", "create", "generates", "implement", "function", "method", "code",
        "program", "script", "snippet", "example", "simple", "basic", "following",
        "python", "javascript", "typescript", "ruby", "julia", "rust", "bash",
        "java", "golang", "csharp", "cplusplus", "sql",
    };

    private static IEnumerable<string> ExtractKeywords(string prompt)
    {
        
        
        int count = 0;
        foreach (var raw in prompt.Split(
            new char[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '-', '_' },
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (count >= 20) break;
            if (raw.Length < 4) continue;
            var w = raw.ToLowerInvariant();
            
            var stem = w.Length > 5 && w.EndsWith('s') ? w[..^1] : w;
            if (!StopWords.Contains(w) && !StopWords.Contains(stem))
            {
                yield return w;
                count++;
            }
        }
    }

    private static string? ResolveModality(string? lang)
    {
        if (string.IsNullOrEmpty(lang)) return null;
        int slash = lang.IndexOf('/');
        if (slash > 0) lang = lang[..slash];
        lang = lang.Trim();
        if (LangModality.TryGetValue(lang, out var m)) return m;
        
        
        if (lang.Contains("cypher", StringComparison.OrdinalIgnoreCase)) return null;
        if (lang.Contains("sql", StringComparison.OrdinalIgnoreCase)) return "sql";
        return null;
    }

    private static async IAsyncEnumerable<(string? ConceptKey, string? Lang, string? Prompt, string? Response)> ReadRowsAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(fs, cancellationToken: ct);

        DataField[] fields = reader.Schema.GetDataFields();
        
        
        DataField? taskField   = FindField(fields, "task_id");
        DataField? langField   = FindField(fields, "programming_language");
        DataField? promptField = FindField(fields, "prompt");
        DataField? respField   = FindField(fields, "response");
        string fileStem = Path.GetFileNameWithoutExtension(path);
        long rowBase = 0;
        if (promptField is null || respField is null || (taskField is null && langField is null))
            throw new InvalidOperationException(
                $"TinyCodesDecomposer: unrecognized parquet schema in '{path}' — "
                + $"need prompt+response and task_id or programming_language; found: "
                + string.Join(", ", fields.Select(f => f.Name)));

        for (int rg = 0; rg < reader.RowGroupCount; rg++)
        {
            ct.ThrowIfCancellationRequested();
            using var rgr = reader.OpenRowGroupReader(rg);
            int count = (int)rgr.RowCount;

            string[]? taskIds = null;
            string[]? langs   = null;
            string[] prompts  = new string[count];
            string[] resps    = new string[count];

            if (taskField is not null)
                await rgr.ReadAsync(taskField, taskIds = new string[count]);
            if (langField is not null)
                await rgr.ReadAsync(langField, langs = new string[count]);
            await rgr.ReadAsync(promptField, prompts);
            await rgr.ReadAsync(respField,   resps);

            for (int i = 0; i < count; i++)
            {
                string? lang = langs?[i];
                string? key  = taskIds?[i] ?? $"{fileStem}/{rowBase + i}";
                yield return (key, lang, prompts[i], resps[i]);
            }
            rowBase += count;
        }
    }

    private static DataField? FindField(DataField[] fields, string name)
    {
        foreach (var f in fields)
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                return f;
        return null;
    }

    private static IEnumerable<string> EnumerateParquet(string root)
    {
        if (File.Exists(root))
        {
            if (root.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase)) yield return root;
            yield break;
        }
        if (!Directory.Exists(root)) yield break;
        foreach (var f in Directory.EnumerateFiles(root, "*.parquet", SearchOption.TopDirectoryOnly)
                                   .OrderBy(p => p, StringComparer.Ordinal))
            yield return f;
    }

    private static SubstrateChangeBuilder NewBuilder(int n, ISubstrateReader? _) =>
        new SubstrateChangeBuilder(Source, $"tiny-codes/{n}", null,
            entityCapacity: 8192, physicalityCapacity: 8192, attestationCapacity: 4096);
}
