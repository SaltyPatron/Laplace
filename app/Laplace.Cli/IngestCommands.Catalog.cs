using System.Text.Json;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Cli;

internal static partial class IngestCommands
{
    private static int PrintIngestCatalog(string[] args)
    {
        if (args.Length > 2 || (args.Length == 2 && args[1] != "--json"))
            return CliRuntime.Fail("usage: laplace-mono ingest catalog [--json]");
        var catalog = EtlManifest.Catalog;
        if (args[0] == "--list-sources")
        {
            foreach (var source in catalog.Sources) Console.WriteLine(source.Name);
            return 0;
        }
        if (args.Contains("--json", StringComparer.Ordinal) || args[0] == "--catalog")
        {
            Console.WriteLine(JsonSerializer.Serialize(catalog, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
            return 0;
        }
        foreach (var source in catalog.Sources)
        {
            string command = source.Aliases.FirstOrDefault() ?? source.Name;
            Console.WriteLine($"{command}: {source.Name}" + (source.Aliases.Count > 0 ? $" (aliases: {string.Join(", ", source.Aliases)})" : ""));
            Console.WriteLine($"  witness: {source.Witness}" + (source.Lineage.Length > 0 ? $"; lineage: {source.Lineage}" : ""));
            Console.WriteLine($"  content: {string.Join("; ", source.Files.Count > 0 ? source.Files : source.Roots)}");
            Console.WriteLine($"  decomposers: {string.Join(", ", source.Recipes.Select(r => r.Name + " [" + r.Grammar + "]").Concat(source.Reads))}");
            if (source.After.Count > 0) Console.WriteLine($"  after: {string.Join(", ", source.After)}");
            if (source.Held.Length > 0) Console.WriteLine($"  held: {source.Held}");
        }
        Console.WriteLine("File decomposers:");
        foreach (var format in catalog.Formats)
            Console.WriteLine($"  {format.Name} [{format.Grammar}]: {string.Join(" ", format.Match)}");
        Console.WriteLine("Operator commands:");
        foreach (string command in IngestDispatchTable.RegisteredKeys.Except(catalog.Sources.Select(s => s.Name), StringComparer.OrdinalIgnoreCase)
            .Where(command => command is not ("model" or "safetensor")).Order(StringComparer.Ordinal))
            Console.WriteLine("  " + command + (command == "safetensors" ? " (aliases: model, safetensor)" : ""));
        return 0;
    }
}
