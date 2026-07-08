using System.Diagnostics;
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
/// Runs Mold-A-Model foundry export via CLI subprocess — writes GGUF (or other format) to disk;
/// never loads weights on the HTTP request path.
/// </summary>
internal sealed class CliFoundryExportService : IFoundryExportService
{
    public async Task<FoundryExportResult> ExportAsync(
        string? recipeJson,
        string? recipeIdPrefix,
        string? tokenizerDir,
        string format,
        string? filename,
        CancellationToken ct)
    {
        if (!LaplaceInstall.TryRepoRoot(out var repoRoot))
            throw new InvalidOperationException("Cannot locate Laplace repo root for foundry export.");

        var cliProj = Path.Combine(repoRoot, "app", "Laplace.Cli", "Laplace.Cli.csproj");
        if (!File.Exists(cliProj))
            throw new InvalidOperationException($"CLI project not found: {cliProj}");

        var workDir = Path.Combine(Path.GetTempPath(), $"laplace-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        string recipePath;
        var args = new List<string> { "run", "--project", cliProj, "--no-build", "--", "synthesize", "substrate" };

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

        var ext = string.Equals(format, "gguf", StringComparison.OrdinalIgnoreCase) ? "gguf" : format.Trim().ToLowerInvariant();
        var outputName = string.IsNullOrWhiteSpace(filename) ? $"laplace-export.{ext}" : filename.Trim();
        var outputPath = Path.Combine(workDir, outputName);
        args.Add(outputPath);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start foundry export subprocess.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(
                $"Foundry export failed (exit {process.ExitCode}): {detail.Trim()}");
        }

        if (!File.Exists(outputPath))
            throw new InvalidOperationException("Foundry export completed but output file was not produced.");

        var bytes = new FileInfo(outputPath).Length;
        return new FoundryExportResult(outputPath, bytes, ext);
    }
}
