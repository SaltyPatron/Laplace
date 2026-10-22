using Laplace.Decomposers.Abstractions;

namespace Laplace.Cli;

/// <summary>Recipe workbench commands execute the same Native job as corpus ingestion.</summary>
internal static class CookbookCommands
{
    internal static Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        NativeRecipeMode? mode = args.FirstOrDefault() switch
        {
            "inspect" or "inventory" or "plan" => NativeRecipeMode.Plan,
            "claims" => NativeRecipeMode.Claims,
            "check" => NativeRecipeMode.NoLoad,
            "ingest" or "ingest-source" => NativeRecipeMode.Ingest,
            _ => null,
        };
        if (mode is null || args.Length is < 2 or > 3)
        {
            Console.Error.WriteLine("usage: cookbook <plan|claims|check|ingest> <canonical-source> [input-path]"
                + "\nRecipes are the .format/.recipe estate declared by LAPLACE_RECIPES.");
            return Task.FromResult(2);
        }
        string source = IngestDispatchTable.CanonicalSourceName(args[1]);
        if (!EtlManifest.SourceOrder.Contains(source, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown canonical recipe source '{args[1]}'.");
        var request = new IngestCommands.IngestCliArgs(source,
            args.Length == 3 ? args[2] : "", "", null, null, false, false, NativeMode: mode.Value);
        return IngestCommands.IngestNativeRecipeAsync(source, request, ct);
    }
}
