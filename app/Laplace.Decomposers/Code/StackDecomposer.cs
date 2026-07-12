using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Code;

public sealed class StackDecomposer : GrammarComposeDecomposer<StackSource, FullScope>
{
    public static readonly Hash128 Source = StackSource.SourceId;
    public static readonly Hash128 TrustClass = StackSource.TrustClass;

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

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "stack-v2";

    private readonly HashSet<string>? _langFilter = null;

    public StackDecomposer() { }

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
