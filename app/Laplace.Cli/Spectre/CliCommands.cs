using System.ComponentModel;
using Spectre.Console.Cli;

namespace Laplace.Cli.Spectre;

internal abstract class ForwardCommand<TSettings> : AsyncCommand<TSettings>
    where TSettings : CommandSettings
{
    protected static string[] Raw(CommandContext ctx) => Enumerable.ToArray(ctx.Remaining.Raw);
    protected static string Joined(CommandContext ctx) => string.Join(' ', ctx.Remaining.Raw);
}

internal class TailSettings : CommandSettings
{
    [CommandArgument(0, "[args]")]
    public string[]? Args { get; init; }
}

[Description("Ingest a source into the substrate (unicode, wordnet, omw, ud, chess, model, …). 'ingest chain \"<spec>\" …' runs several in sequence.")]
internal sealed class IngestCommand : ForwardCommand<IngestCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[args]")]
        [Description("Registered source name, then an optional file/dir/ecosystem path (source-defaulted if omitted).")]
        public string[]? Args { get; init; }

        [CommandOption("--catalog")][Description("List all recipe sources and decomposers as JSON without connecting to the database.")] public bool Catalog { get; init; }
        [CommandOption("--list-sources")][Description("List all canonical recipe source names.")] public bool ListSources { get; init; }
        [CommandOption("--json")][Description("Print the ingestion catalog as JSON.")] public bool Json { get; init; }
        [CommandOption("--plan")][Description("Inspect the canonical Native recipe plan without loading records.")] public bool Plan { get; init; }
        [CommandOption("--claims")][Description("Show claims from explicitly selected inputs without loading them.")] public bool Claims { get; init; }
        [CommandOption("--no-load")][Description("Execute Native parsing and validation without database writes.")] public bool NoLoad { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext ctx, Settings s, CancellationToken ct)
        => IngestCommands.IngestAsync(Raw(ctx));
}

[Description("Inspect how a piece of text resolves in the substrate.")]
internal sealed class InspectCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => QueryCommands.InspectAsync(Joined(ctx));
}

[Description("Converse with the substrate. No prompt: interactive REPL (one connection, one session).")]
internal sealed class ConverseCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => QueryCommands.ConverseAsync(Joined(ctx));
}

[Description("Recall the substrate's answer to a goal.")]
internal sealed class RecallCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => QueryCommands.RecallAsync(Joined(ctx));
}

[Description("Plural nearest-neighbors of a word: structural geodesic + shape Fréchet + semantic μ.")]
internal sealed class NeighborsCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => QueryCommands.NeighborsAsync(Joined(ctx));
}

[Description("n-gram stride backoff over witnessed trajectories. No prompt: interactive REPL.")]
internal sealed class WalkCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => QueryCommands.WalkAsync(Raw(ctx));
}

[Description("Chat over the substrate's own token prediction (no conventional AI).")]
internal sealed class ChatCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => QueryCommands.ChatAsync(Raw(ctx));
}

[Description("Feedback: confirm or refute one claim, subject relation [[object]], stated by the operator as a runtime witness (Engine laplace witness).")]
internal sealed class AttestCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => QueryCommands.AttestAsync(Raw(ctx));
}

[Description("Chess lab. Subcommands: lab, match, selfplay, move, fetch, record, substrate-test, ladder, review, tactics, lichess, bench. Run 'chess' for the full flag reference.")]
internal sealed class ChessCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => ChessCommands.RunAsync(Raw(ctx));
}

[Description("Substrate row-count inventory and health. Optional source key runs exact source diagnostics.")]
internal sealed class StatsCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
    {
        var raw = Raw(ctx);
        if (raw.Length > 1)
        {
            Console.Error.WriteLine("usage: stats [[cli-source]]");
            return Task.FromResult(2);
        }
        return IngestCommands.StatsAsync(raw.Length == 1 ? raw[0] : null);
    }
}

[Description("CPU topology probe. Flags: --p-cores, --cpu-bound-workers, --io-bound-workers, --p-core-indices, --e-core-indices, --pg-tuning, --verify-pin.")]
internal sealed class CpuTopologyCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => Task.FromResult(CpuTopologyCommands.Run(Raw(ctx)));
}
