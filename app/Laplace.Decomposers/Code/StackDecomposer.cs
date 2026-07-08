using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Code;

public sealed class StackDecomposer : GrammarComposeDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/StackDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly Dictionary<string, string?> StackLangToModality =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Python"] = "python",
            ["C"] = "c",
            ["C++"] = "cpp",
            ["JavaScript"] = "javascript",
            ["TypeScript"] = "typescript",
            ["Rust"] = "rust",
            ["Go"] = "go",
            ["C#"] = "c-sharp",
            ["Shell"] = "bash",
            ["Java"] = "java",
            ["Ruby"] = "ruby",
            ["Julia"] = "julia",
            ["Kotlin"] = "kotlin",
            ["Swift"] = "swift",
            ["PHP"] = "php",
            ["SQL"] = "sql",
            ["JSON"] = "json",
            ["Markdown"] = "markdown",
            ["TypeScript JSX"] = null,
            ["JavaScript JSX"] = null,
            ["HTML"] = null,
            ["CSS"] = null,
            ["SCSS"] = null,
            ["Dockerfile"] = null,
            ["YAML"] = null,
            ["TOML"] = null,
        };

    public override Hash128 SourceId => Source;
    public override string SourceName => "StackDecomposer";
    public override int LayerOrder => 2;
    public override Hash128 TrustClassId => TrustClass;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "stack-v2";

    private readonly HashSet<string>? _langFilter = null;

    public StackDecomposer() { }

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            relationNodeNames: ["HAS_EXAMPLE", "HAS_DEFINITION", "CALLS", "DEFINES", "REFERENCES"],
            ct: ct);

    protected override async IAsyncEnumerable<GrammarComposeRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var files = SharedParquetRecordStream.EnumerateParquet(ecosystemPath, SearchOption.AllDirectories).ToList();
        if (files.Count == 0)
        {
            if (Directory.Exists(ecosystemPath))
                throw new InvalidOperationException(
                    $"StackDecomposer: no *.parquet files under '{ecosystemPath}' "
                    + "(expected data/<lang>/train-*.parquet from download-code-data.cmd)");
            yield break;
        }

        foreach (var file in files)
        {
            await foreach (var row in SharedParquetRecordStream.ReadStackRowsAsync(file, ct))
            {
                ct.ThrowIfCancellationRequested();
                string? modality = ResolveModality(row.Language);
                if (modality is null) continue;
                if (options.Languages?.IsActive == true && !options.Languages.MatchesRaw(modality)) continue;
                if (_langFilter is not null && !_langFilter.Contains(modality)) continue;
                if (string.IsNullOrWhiteSpace(row.Content)) continue;
                byte[] codeBytes = Encoding.UTF8.GetBytes(row.Content);
                if (codeBytes.Length == 0) continue;

                IReadOnlyList<string>? segs = null;
                if (!string.IsNullOrWhiteSpace(row.Path))
                {
                    var filename = Path.GetFileNameWithoutExtension(row.Path);
                    segs = filename.Split(
                            ['_', '-', '.', '/', '\\'],
                            StringSplitOptions.RemoveEmptyEntries)
                        .Where(s => s.Length >= 3)
                        .Select(s => s.ToLowerInvariant())
                        .ToList();
                }

                yield return new GrammarComposeRecord(codeBytes, modality, segs);
            }
        }
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(null);

    private static string? ResolveModality(string? lang)
    {
        if (string.IsNullOrEmpty(lang)) return null;
        return StackLangToModality.TryGetValue(lang, out var m) ? m : null;
    }
}
