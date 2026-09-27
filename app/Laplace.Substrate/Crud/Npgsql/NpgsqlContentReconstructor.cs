using Laplace.Engine.Core;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// REALIZE for text content: <c>realize.reconstruct_content</c> walks the composition DAG
/// natively, assembles the UTF-8 bytes and verifies they re-hash to the requested id; this
/// class binds the id and optional modality and returns the bytes.
///
/// Plain-text content returns canonical normalized UTF-8; content composed under a declared
/// source grammar returns its source-preserving UTF-8. A null result (absent, incomplete,
/// cyclic, non-text or identity mismatch) throws.
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
