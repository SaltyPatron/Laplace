using Laplace.Engine.Core;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Exact canonical UTF-8 reconstruction from one text-content id. The database operation owns
/// the complete cycle-safe DAG walk, native assembly, and identity check; this managed surface
/// only binds the id and transports the returned bytes.
///
/// Plain-text admission returns canonical normalized UTF-8. A declared source grammar
/// returns its source-preserving UTF-8 representation. Other encodings and container
/// packaging require their own artifact reconstruction operation.
/// </summary>
public static class NpgsqlContentReconstructor
{
    public static async Task<byte[]> ReconstructUtf8Async(
        NpgsqlDataSource dataSource,
        Hash128 contentId,
        CancellationToken ct = default)
        => await ReconstructUtf8Async(dataSource, contentId, null, ct).ConfigureAwait(false);

    public static async Task<byte[]> ReconstructUtf8Async(
        NpgsqlDataSource dataSource,
        Hash128 contentId,
        string? modality,
        CancellationToken ct = default)
    {
        byte[]? reconstructed = await NpgsqlRead.ExecuteScalarAsync<byte[]>(
            dataSource,
            "SELECT realize.reconstruct_content(@id, @modality)",
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = contentId.ToBytes();
                p.Add("modality", NpgsqlDbType.Text).Value = (object?)modality ?? DBNull.Value;
            },
            ct: ct,
            label: "reconstruct_content").ConfigureAwait(false);

        return reconstructed ?? throw new InvalidDataException(
            $"content {contentId} is absent, incomplete, cyclic, non-text, or failed canonical identity verification");
    }
}
