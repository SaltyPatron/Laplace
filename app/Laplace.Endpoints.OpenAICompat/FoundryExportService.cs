using Laplace.Decomposers.Model;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed record FoundryExportResult(string OutputPath, long Bytes, string Format);

internal interface IFoundryExportService
{
    Task<FoundryExportResult> ExportAsync(
        string? recipeJson,
        string? recipeIdPrefix,
        string? tokenizerDir,
        string format,
        string? filename,
        CancellationToken ct);
}

/// <summary>
/// Uses the same native construction and codecs as the operator CLI.
/// </summary>
internal sealed class NativeFoundryExportService : IFoundryExportService
{
    /// <summary>Formats this route writes; any other requested format is refused.</summary>
    internal static readonly HashSet<string> WritableFormats =
        new(StringComparer.OrdinalIgnoreCase) { "gguf", "safetensors" };


    public async Task<FoundryExportResult> ExportAsync(
        string? recipeJson,
        string? recipeIdPrefix,
        string? tokenizerDir,
        string format,
        string? filename,
        CancellationToken ct)
    {
        // Only formats in WritableFormats are accepted; the default is gguf.
        string requested = (format ?? "").Trim();
        if (requested.Length == 0) requested = "gguf";
        if (!WritableFormats.Contains(requested))
            throw new ArgumentException(
                $"foundry export cannot produce '{requested}'. Writable formats: "
                + string.Join(", ", WritableFormats.Order(StringComparer.Ordinal))
                + ".",
                nameof(format));
        var ext = requested.ToLowerInvariant();

        var workDir = Path.Combine(Path.GetTempPath(), $"laplace-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        string recipePath;
        var args = new List<string> { "substrate", "--format", ext };

        if (!string.IsNullOrWhiteSpace(recipeIdPrefix))
        {
            if (string.IsNullOrWhiteSpace(tokenizerDir) || !Directory.Exists(tokenizerDir))
                throw new ArgumentException("Field 'tokenizer_dir' is required when using 'recipe_id_prefix'.");
            args.Add("--recipe-from");
            args.Add(recipeIdPrefix.Trim());
            args.Add("--tokenizer");
            args.Add(Path.GetFullPath(tokenizerDir.Trim()));
            recipePath = "";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(recipeJson))
                throw new ArgumentException("Field 'recipe' is required unless 'recipe_id_prefix' is set.");
            recipePath = Path.Combine(workDir, "recipe.json");
            await File.WriteAllTextAsync(recipePath, recipeJson.Trim(), ct);
            args.Add(recipePath);
        }

        var outputName = string.IsNullOrWhiteSpace(filename) ? $"laplace-export.{ext}" : filename.Trim();
        if (Path.GetFileName(outputName) != outputName || outputName is "." or "..")
            throw new ArgumentException("filename must be a filename inside the export directory.", nameof(filename));
        var outputPath = Path.Combine(workDir, outputName);
        args.Add(outputPath);

        ct.ThrowIfCancellationRequested();
        int status = await FoundrySynthesis.SynthesizeAsync(args.ToArray());
        ct.ThrowIfCancellationRequested();
        if (status != 0) throw new InvalidOperationException($"Native model export failed (status {status}).");
        if (!File.Exists(outputPath) && !Directory.Exists(outputPath))
            throw new InvalidOperationException("Native model export produced no artifact.");
        var bytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length
            : Directory.EnumerateFiles(outputPath, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
        return new FoundryExportResult(outputPath, bytes, ext);
    }
}
