using System.ComponentModel;
using Spectre.Console.Cli;

namespace Laplace.Cli.Spectre;

// Spectre command layer for the Laplace CLI (GH #603).
//
// SAFE EXECUTION: the entrypoint injects a `--` after the command token, so every real
// argument lands in ctx.Remaining.Raw verbatim and is handed to the EXISTING command
// method unchanged — the battle-tested parsers in IngestCommands/QueryCommands/ChessCommands/
// … stay the single source of truth, so behavior is byte-identical to the pre-Spectre switch
// (a naive Spectre passthrough silently drops interleaved flags — proven — which is why we do
// not let Spectre bind the real args). The settings classes model the documented options for
// `--help` display and command discovery ONLY; they are intentionally not read at execution.
//
// Each Execute mirrors exactly the old Program.cs switch arm, sourcing from Raw(ctx)/Joined(ctx)
// where the arm used args[1..] / string.Join(' ', args[1..]).

internal abstract class ForwardCommand<TSettings> : AsyncCommand<TSettings>
    where TSettings : CommandSettings
{
    protected static string[] Raw(CommandContext ctx) => System.Linq.Enumerable.ToArray(ctx.Remaining.Raw);
    protected static string Joined(CommandContext ctx) => string.Join(' ', ctx.Remaining.Raw);
}

/// <summary>Settings with no modeled options — a bare tail hint for help.</summary>
internal class TailSettings : CommandSettings
{
    [CommandArgument(0, "[args]")]
    public string[]? Args { get; init; }
}

// ---- ingest ---------------------------------------------------------------------------------

[Description("Ingest a source into the substrate (unicode, wordnet, omw, ud, chess, model, …). 'ingest chain \"<spec>\" …' runs several in sequence.")]
internal sealed class IngestCommand : ForwardCommand<IngestCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[args]")]
        [Description("Registered source name, then an optional file/dir/ecosystem path (source-defaulted if omitted).")]
        public string[]? Args { get; init; }

        [CommandOption("--recursive")][Description("Recurse into nested corpora (multi-file sources).")] public bool Recursive { get; init; }
        [CommandOption("--force")][Description("Re-observe content already proven present.")] public bool Force { get; init; }
        [CommandOption("--no-analyze")][Description("chess: record game-grain only; defer derivation to chess-analyze.")] public bool NoAnalyze { get; init; }
        [CommandOption("--no-evidence")][Description("Skip evidence attestations (structure only).")] public bool NoEvidence { get; init; }
        [CommandOption("--register-only")][Description("Register canonical ids only; no fold.")] public bool RegisterOnly { get; init; }
        [CommandOption("--emit-cross-lang")][Description("Emit cross-language links.")] public bool EmitCrossLang { get; init; }
        [CommandOption("--langs <SPEC>")][Description("Language filter spec.")] public string? Langs { get; init; }
        [CommandOption("--depth <N>")][Description("chess-analyze/eval: per-position engine search depth.")] public int Depth { get; init; }
        [CommandOption("--nodes <N>")][Description("chess-eval: node-capped search budget.")] public long Nodes { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext ctx, Settings s, CancellationToken ct)
        => IngestCommands.IngestAsync(Raw(ctx));
}

// ---- document / synthesize ------------------------------------------------------------------

[Description("Extract a recipe's provenance.json (the canonical source material) to an output directory.")]
internal sealed class DocumentCommand : ForwardCommand<DocumentCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[args]")]
        [Description("Recipe path, then an optional output directory (defaults beside the recipe).")]
        public string[]? Args { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext ctx, Settings s, CancellationToken ct)
        => DocumentCommands.RunAsync(Raw(ctx));
}

[Description("Mold a runnable transformer from consensus+geometry. Subcommand: substrate. Flags incl. --scope-source, --recipe-from, --native-vocab/--dim/--layers/--heads/--kv-heads/--ffn, --tokenizer, --crawl/--hops/--fanout.")]
internal sealed class SynthesizeCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => FoundryCommands.SynthesizeAsync(Raw(ctx));
}

// ---- text / query lane (join the tail into one string, as the old switch did) ---------------

[Description("Decompose text into the substrate's record stream (no DB write).")]
internal sealed class DecomposeCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => Task.FromResult(DecompositionCommands.Decompose(Joined(ctx)));
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

[Description("OODA feedback: confirm/refute a token sequence (PRECEDES) or one consensus triple (subject RELATION object).")]
internal sealed class AttestCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => QueryCommands.AttestAsync(Raw(ctx));
}

// ---- chess (self-dispatching subcommands, unchanged) ----------------------------------------

[Description("Chess lab. Subcommands: match (engine-vs-engine, live terminal board), selfplay, move, fetch, substrate-test, ladder, review, learned-pst, learned-eval-test, tactics, lichess. Run 'chess' for the full flag reference.")]
internal sealed class ChessCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => ChessCommands.RunAsync(Raw(ctx));
}

// ---- decompose roundtrips / eval / benches --------------------------------------------------

[Description("Roundtrip a file through content-addressing and rebuild its bytes.")]
internal sealed class RoundtripCommand : ForwardCommand<RoundtripCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[args]")]
        [Description("Input file, then an optional output path for the rebuilt bytes.")]
        public string[]? Args { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext ctx, Settings s, CancellationToken ct)
    {
        var raw = Raw(ctx);
        return Task.FromResult(DecompositionCommands.Roundtrip(
            raw.Length > 0 ? raw[0] : "", raw.Length > 1 ? raw[1] : null));
    }
}

[Description("Roundtrip a file through the database (ingest then rebuild from stored content).")]
internal sealed class DbRoundtripCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
    {
        var raw = Raw(ctx);
        return DecompositionCommands.DbRoundtripAsync(raw.Length > 0 ? raw[0] : "");
    }
}

[Description("Evaluation harness. Subcommand: ingest-fidelity [[relation]] [[ground-truth]] [[n]] — AUC of a model plane vs seed relations.")]
internal sealed class EvalCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => EvalCommands.RunAsync(Raw(ctx));
}

[Description("Substrate row-count inventory and health. Optional source key runs exact source diagnostics.")]
internal sealed class StatsCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
    {
        var raw = Raw(ctx);
        if (raw.Length > 1)
        {
            Console.Error.WriteLine("usage: stats [cli-source]");
            return Task.FromResult(2);
        }
        return IngestCommands.StatsAsync(raw.Length == 1 ? raw[0] : null);
    }
}

[Description("Close a cut-off ingest journal row: close-run <run_id> [cancelled|failed].")]
internal sealed class CloseRunCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
    {
        var raw = Raw(ctx);
        if (raw.Length is not (1 or 2))
        {
            Console.Error.WriteLine("usage: close-run <run_id> [cancelled|failed]");
            return Task.FromResult(2);
        }
        return IngestCommands.CloseRunAsync(raw[0], raw.Length == 2 ? raw[1] : "cancelled");
    }
}

[Description("Verify a source's relation-law bootstrap rows landed (#760 positive control). The law relation name is the operator's declaration, e.g. the name-alias relation.")]
internal sealed class SourceBootstrapCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
    {
        var raw = Raw(ctx);
        if (raw.Length != 2)
        {
            Console.Error.WriteLine("usage: source-bootstrap <SourceName> <LAW_RELATION>");
            return Task.FromResult(2);
        }
        return IngestCommands.SourceBootstrapAsync(raw[0], raw[1]);
    }
}

[Description("Run a command holding the measurement lane exclusive, so no ingest writes while it is timed. Usage: measure-lane <cmd> [args…]")]
internal sealed class MeasureLaneCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
    {
        var raw = Raw(ctx);
        if (raw.Length == 0)
        {
            Console.Error.WriteLine("usage: measure-lane <command> [args…]");
            return Task.FromResult(2);
        }
        return Laplace.SubstrateCRUD.Npgsql.MeasurementLane.RunExclusiveAsync(raw[0], raw[1..], ct);
    }
}

[Description("Prove tensor_svd_truncate is fp-exact on a real tensor (no DB).")]
internal sealed class SvdExactBenchCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => Task.FromResult(BenchCommands.SvdExactBenchCmd(Raw(ctx)));
}

[Description("Run the whole-model FFN/relation ETL on a real model (no DB).")]
internal sealed class ModelBenchCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => BenchCommands.ModelBenchCmd(Raw(ctx));
}

// ---- evict ----------------------------------------------------------------------------------

[Description("Lawfully retract a source's testimony (#508): delete its evidence per relation partition, refold every touched consensus cell from the surviving rows (zero-survivor cells deleted, never zeroed), queue+drain highway-mask repair, and delete the lane's derivation markers so a --rederive re-runs it at the new version without double-counting. Args: <sourceName> [--relations A,B] [--marker-types X,Y] [--rederive].")]
internal sealed class EvictCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => EvictCommands.EvictAsync(Raw(ctx));
}

// ---- index maintenance (no args) ------------------------------------------------------------

[Description("Rebuild physicality indexes.")]
internal sealed class RebuildPhysIndexesCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => IngestCommands.RebuildPhysIndexesAsync();
}

[Description("Recover indexes left absent by a legacy interrupted index-cycle run.")]
internal sealed class RecoverIndexesCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => IngestCommands.RecoverCycledIndexesAsync();
}

// ---- cpu-topology ---------------------------------------------------------------------------

[Description("CPU topology probe. Flags: --p-cores, --cpu-bound-workers [[headroom]], --io-bound-workers, --p-core-indices, --e-core-indices, --pg-tuning, --verify-pin.")]
internal sealed class CpuTopologyCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings s, CancellationToken ct)
        => Task.FromResult(CpuTopologyCommands.Run(Raw(ctx)));
}
