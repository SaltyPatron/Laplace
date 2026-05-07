namespace Laplace.Cli;

/// <summary>
/// laplace.exe entry point. Phase 1 / Track A — minimal scaffolding only.
/// Real subcommand routing (ingest-{modality}, recompose-{modality},
/// export-model, query, traverse, voronoi, intersections, frayed-edges,
/// godel-task, seed-foundational, seed-secondary, db-bootstrap, db-reset,
/// status, audit) lands in Phase 6 / Track K once the underlying services
/// exist to call.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            Console.WriteLine("laplace — substrate CLI (Phase 1 scaffold)");
            Console.WriteLine();
            Console.WriteLine("Subcommands land in Phase 6 / Track K. Currently no operations are wired.");
            Console.WriteLine();
            Console.WriteLine("See:");
            Console.WriteLine("  README.md");
            Console.WriteLine("  docs/substrate-synthesis.md");
            Console.WriteLine("  ~/.claude/plans/time-for-you-to-scalable-wind.md");
            return 0;
        }

        Console.Error.WriteLine($"Unknown subcommand '{args[0]}'. Subcommand routing is a Phase 6 / Track K deliverable.");
        return 64; // EX_USAGE
    }
}
