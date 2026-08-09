using Laplace.Api.Contracts;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The live scoreboard + modality reads. Thin callers over the installed
/// ops.substrate_pulse() / ops.modality_counts() — the set logic lives in the
/// extension (one implementation; the MCP server reads the same functions),
/// C# only maps rows. The SQL itself lives in
/// <see cref="NpgsqlSubstrateReads"/> (doc 41).
/// </summary>
internal sealed partial class SubstrateClient
{
    public async Task<ModalitiesResponse> ModalitiesAsync(CancellationToken ct)
    {
        var (text, chess, models, multilingual, documents) =
            await NpgsqlSubstrateReads.ModalityCountsAsync(_dataSource, ct, TranslateSubstrateError);
        return new ModalitiesResponse("modalities", text, chess, models, multilingual, documents);
    }

    public async Task<PulseResponse> PulseAsync(long nowUnix, CancellationToken ct)
    {
        var pulse = await NpgsqlSubstrateReads.SubstratePulseAsync(_dataSource, ct, TranslateSubstrateError);
        if (pulse is not { } p)
            return new PulseResponse("pulse", nowUnix, 0, 0, 0, 0, null, 0, false);
        return new PulseResponse("pulse", nowUnix,
            p.Entities, p.Attestations, p.Consensus, p.Physicalities,
            p.LastFlushUnix, p.FlushesLastMin, p.Folding);
    }
}
