using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Cli;

internal static class ContentRoundtrip
{
    public static Hash128 PromptSource => UserPromptContent.Source;

    public static Task BootstrapAsync(ISubstrateWriter writer, CancellationToken ct = default)
        => writer.ApplyAsync(UserPromptContent.BuildBootstrapChange(), ct);

    public static async Task<Hash128> RecordAsync(
        ISubstrateWriter writer, byte[] utf8, CancellationToken ct = default)
    {
        if (!UserPromptContent.TryBuildWitnessChange(utf8, "prompt", out var change, out var rootId))
            return Hash128.Zero;
        await writer.ApplyAsync(change, ct);
        return rootId;
    }

    public static Task<byte[]> ReconstructAsync(
        NpgsqlDataSource ds, Hash128 documentId, CancellationToken ct = default)
        => NpgsqlContentReconstructor.ReconstructUtf8Async(ds, documentId, ct);
}
