using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Code;

public sealed class TinyCodesDecomposer : GrammarComposeDecomposer<TinyCodesSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = TinyCodesSource.SourceId;
    public static readonly Hash128 TrustClass = TinyCodesSource.TrustClass;

    private static readonly Hash128 CodeConceptTypeId = EntityTypeRegistry.CodeConcept;

    private static readonly Dictionary<string, string?> LangModality =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["python"] = "python",
            ["javascript"] = "javascript",
            ["typescript"] = "typescript",
            ["ruby"] = "ruby",
            ["julia"] = "julia",
            ["rust"] = "rust",
            ["c++"] = "cpp",
            ["bash"] = "bash",
            ["java"] = "java",
            ["c#"] = "c-sharp",
            ["go"] = "go",
            ["sql"] = "sql",
            ["cypher"] = null,
        };

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "tiny-codes";

    private readonly ConcurrentDictionary<string, byte> _canonicalNames = new(StringComparer.Ordinal);
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames.Keys.ToArray();

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => _canonicalNames;

    protected override async IAsyncEnumerable<GrammarComposeRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var files = SharedParquetRecordStream.EnumerateParquet(ecosystemPath, SearchOption.TopDirectoryOnly).ToList();
        if (files.Count == 0)
        {
            if (Directory.Exists(ecosystemPath))
                throw new InvalidOperationException(
                    $"TinyCodesDecomposer: no *.parquet files under '{ecosystemPath}' "
                    + "(expected top-level shards from download-code-data.cmd)");
            yield break;
        }

        foreach (var file in files)
        {
            await foreach (var (conceptKey, lang, prompt, response) in SharedParquetRecordStream.ReadTinyCodesRowsAsync(file, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(response)) continue;

                string? modality = ResolveModality(lang);
                if (modality is null) continue;

                byte[] codeBytes = Encoding.UTF8.GetBytes(response);
                if (codeBytes.Length == 0) continue;

                IReadOnlyList<string>? keywords = string.IsNullOrWhiteSpace(prompt)
                    ? null
                    : ExtractKeywords(prompt).ToList();

                yield return new GrammarComposeRecord(
                    codeBytes,
                    modality,
                    ConceptAnchorKey: string.IsNullOrEmpty(conceptKey) ? null : conceptKey,
                    ConceptCategoryTypeId: CodeConceptTypeId,
                    KeywordExamples: keywords);
            }
        }
    }

    /// <summary>
    /// Exact inventory from parquet metadata (row-group headers, no data
    /// decode). Without this the runner logs input_units=0 and a 118MB shard
    /// ingests blind — "intents=0/N pct=0.0" with no denominator for hours.
    /// </summary>
    public async Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var files = SharedParquetRecordStream
            .EnumerateParquet(context.EcosystemPath, SearchOption.TopDirectoryOnly).ToList();
        if (files.Count == 0) return null;

        var specs = new List<IngestFileSpec>(files.Count);
        long total = 0;
        foreach (var f in files)
        {
            long n = await SharedParquetRecordStream.CountRowsAsync(f, ct);
            specs.Add(new IngestFileSpec(Path.GetFileName(f), f, n));
            total += n;
        }
        if (options.MaxInputUnits > 0) total = Math.Min(total, options.MaxInputUnits);
        return new IngestInventory("rows", total, specs);
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.ForWitness(SourceName), ct);
        return inv?.TotalInputUnits;
    }

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
            [' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '-', '_'],
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
}
