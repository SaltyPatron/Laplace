using System.ComponentModel;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Spectre.Console.Cli;

namespace Laplace.Cli.Spectre;

[Description("Read the selected substrate's canonical Native inventory. Optional: --joins.")]
internal sealed class StatusCommand : ForwardCommand<TailSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext ctx, TailSettings settings, CancellationToken ct)
    {
        var args = Raw(ctx);
        if (args.Length > 1 || (args.Length == 1 && args[0] != "--joins"))
        {
            Console.Error.WriteLine("usage: status [--joins]");
            return Task.FromResult(2);
        }
        return NativeRecipeExecution.StatusAsync(
            NativeRecipeExecution.ConnectionInfoFromNpgsql(LaplaceInstall.PostgresConnectionString()), args.Length == 1,
            Console.OpenStandardOutput(), Console.OpenStandardError(), ct);
    }
}
