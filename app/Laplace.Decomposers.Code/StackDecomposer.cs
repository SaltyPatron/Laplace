using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Parquet;
using Parquet.Schema;

namespace Laplace.Decomposers.Code;













public sealed class StackDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/StackDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    
    
    private static readonly Dictionary<string, string?> StackLangToModality =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Python"]     = "python",
            ["C"]          = "c",
            ["C++"]        = "cpp",
            ["JavaScript"] = "javascript",
            ["TypeScript"] = "typescript",
            ["Rust"]       = "rust",
            ["Go"]         = "go",
            ["C#"]         = "c-sharp",
            ["Shell"]      = "bash",
            ["Java"]       = "java",
            ["Ruby"]       = "ruby",
            ["Julia"]      = "julia",
            ["Kotlin"]     = "kotlin",
            ["Swift"]      = "swift",
            ["PHP"]        = "php",
            ["SQL"]        = "sql",
            ["JSON"]       = "json",
            ["Markdown"]   = "markdown",
            
            ["TypeScript JSX"] = null,
            ["JavaScript JSX"] = null,
            ["HTML"]           = null,
            ["CSS"]            = null,
            ["SCSS"]           = null,
            ["Dockerfile"]     = null,
            ["YAML"]           = null,
            ["TOML"]           = null,
        };

    public Hash128 SourceId     => Source;
    public string  SourceName   => "StackDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    private readonly HashSet<string>? _langFilter;

    public StackDecomposer()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_STACK_LANGS");
        if (!string.IsNullOrWhiteSpace(env))
        {
            _langFilter = new HashSet<string>(
                env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            relationNodeNames: ["HAS_EXAMPLE", "HAS_DEFINITION", "CALLS", "DEFINES", "REFERENCES"],
            ct: ct);

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
                    $"StackDecomposer: no *.parquet files under '{context.EcosystemPath}' "
                    + "(expected data/<lang>/train-*.parquet from download-code-data.cmd)");
            yield break;
        }

        int batch = options.BatchSize > 1 ? options.BatchSize : 512;
        var reader = context.Reader;
        var b = NewBuilder(0, reader);
        int inBatch = 0, bn = 0;

        foreach (var file in files)
        {
            await foreach (var row in ReadRowsAsync(file, ct))
            {
                ct.ThrowIfCancellationRequested();

                string? modality = ResolveModality(row.Language);
                if (modality is null) continue;
                if (_langFilter is not null && !_langFilter.Contains(modality)) continue;

                IntPtr recipe = GrammarDecomposer.LookupById(modality);
                if (recipe == IntPtr.Zero) continue;

                if (string.IsNullOrWhiteSpace(row.Content)) continue;
                byte[] codeBytes;
                try { codeBytes = Encoding.UTF8.GetBytes(row.Content); }
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
                }
                catch { continue; }

                if (codeRootId == default) continue;

                foreach (var e in ents) b.AddEntity(e);
                foreach (var p in phys) b.AddPhysicality(p);
                foreach (var a in atts) b.AddAttestation(a);

                
                if (!string.IsNullOrWhiteSpace(row.Path))
                {
                    var filename = Path.GetFileNameWithoutExtension(row.Path);
                    foreach (var seg in filename.Split(
                        new char[] { '_', '-', '.', '/', '\\' },
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

    private static string? ResolveModality(string? lang)
    {
        if (string.IsNullOrEmpty(lang)) return null;
        return StackLangToModality.TryGetValue(lang, out var m) ? m : null;
    }

    private static async IAsyncEnumerable<StackRow> ReadRowsAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(fs, cancellationToken: ct);

        DataField[] fields = reader.Schema.GetDataFields();
        DataField? contentField    = FindField(fields, "content");
        DataField? languageField   = FindField(fields, "language") ?? FindField(fields, "lang");
        DataField? pathField       = FindField(fields, "path") ?? FindField(fields, "max_stars_repo_path");
        DataField? vendorField     = FindField(fields, "is_vendor");
        DataField? generatedField  = FindField(fields, "is_generated");

        if (contentField is null || languageField is null) yield break;

        for (int rg = 0; rg < reader.RowGroupCount; rg++)
        {
            ct.ThrowIfCancellationRequested();
            using var rgr = reader.OpenRowGroupReader(rg);
            int count = (int)rgr.RowCount;

            string[] contents  = new string[count];
            string[] languages = new string[count];
            string[]? paths    = pathField is not null ? new string[count] : null;
            bool?[]? vendors    = vendorField is not null ? new bool?[count] : null;
            bool?[]? generated  = generatedField is not null ? new bool?[count] : null;

            await rgr.ReadAsync(contentField,  contents);
            await rgr.ReadAsync(languageField, languages);
            if (paths    is not null) await rgr.ReadAsync(pathField!,       paths);
            if (vendors  is not null) await rgr.ReadAsync<bool>(vendorField!,    vendors);
            if (generated is not null) await rgr.ReadAsync<bool>(generatedField!, generated);

            for (int i = 0; i < count; i++)
            {
                if (vendors  is not null && vendors[i] == true)   continue;
                if (generated is not null && generated[i] == true) continue;
                yield return new StackRow(
                    contents[i],
                    languages[i],
                    paths is not null ? paths[i] : null);
            }
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
        foreach (var f in Directory.EnumerateFiles(root, "*.parquet", SearchOption.AllDirectories)
                                   .OrderBy(p => p, StringComparer.Ordinal))
            yield return f;
    }

    private static SubstrateChangeBuilder NewBuilder(int n, ISubstrateReader? _) =>
        new SubstrateChangeBuilder(Source, $"stack-v2/{n}", null,
            entityCapacity: 8192, physicalityCapacity: 8192, attestationCapacity: 4096);

    private readonly record struct StackRow(string? Content, string? Language, string? Path);
}
