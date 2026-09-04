using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed partial class SubstrateClient
{
    public async Task<UserContentExportResponse?> ExportUserContentAsync(
        string tenant,
        string idHex,
        CancellationToken ct)
    {
        byte[]? requested = ParseEntityId(idHex);
        if (requested is null || !ConversationContent.IsValidIdentifier(tenant))
            return null;

        var userArtifacts = UserArtifactContent.Resolve(tenant);
        var conversation = ConversationContent.Resolve(tenant);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var entity = await ReadEntityOwnerAsync(conn, requested, ct).ConfigureAwait(false);
        if (entity is null) return null;

        if (entity.Value.TypeId.SequenceEqual(EntityTypeRegistry.SourceFile.ToBytes())
            && entity.Value.FirstObservedBy.SequenceEqual(userArtifacts.Source.ToBytes()))
        {
            var vertices = await NpgsqlSubstrateReads.PackedTrajectoryVerticesAsync(
                conn, requested, ct).ConfigureAwait(false);
            var ordered = vertices.OrderBy(static v => v.Ordinal).ToArray();
            if (ordered.Length < 2) return null;

            byte[] contentIdBytes = Convert.FromHexString(ordered[0].ChildIdHex);
            byte[] metadataIdBytes = Convert.FromHexString(ordered[1].ChildIdHex);
            Hash128 contentId = ReadHash(contentIdBytes);
            Hash128 metadataId = ReadHash(metadataIdBytes);
            Hash128 documentId = DocumentEntity.Resolve(contentId);

            byte[] content = await NpgsqlContentReconstructor.ReconstructUtf8Async(
                _dataSource, contentId, ct).ConfigureAwait(false);
            byte[] metadata = await NpgsqlContentReconstructor.ReconstructUtf8Async(
                _dataSource, metadataId, ct).ConfigureAwait(false);
            var (name, path) = ParseFileMetadata(metadata);

            return new UserContentExportResponse(
                Kind: "document",
                RequestedId: idHex.ToLowerInvariant(),
                FileId: idHex.ToLowerInvariant(),
                DocumentId: documentId.ToString(),
                ContentId: contentId.ToString(),
                MetadataId: metadataId.ToString(),
                SourceId: userArtifacts.Source.ToString(),
                Source: userArtifacts.SourceName,
                Name: name,
                Path: path,
                ContentBase64: Convert.ToBase64String(content),
                Text: Encoding.UTF8.GetString(content),
                Contexts: []);
        }

        var promptContexts = await ReadPromptContextsAsync(
            conn, requested, conversation.PromptSource.ToBytes(), ct).ConfigureAwait(false);
        if (promptContexts.Count == 0) return null;

        Hash128 promptId = ReadHash(requested);
        byte[] prompt = await NpgsqlContentReconstructor.ReconstructUtf8Async(
            _dataSource, promptId, ct).ConfigureAwait(false);
        return new UserContentExportResponse(
            Kind: "prompt",
            RequestedId: idHex.ToLowerInvariant(),
            FileId: null,
            DocumentId: null,
            ContentId: promptId.ToString(),
            MetadataId: null,
            SourceId: conversation.PromptSource.ToString(),
            Source: conversation.PromptSourceName,
            Name: null,
            Path: null,
            ContentBase64: Convert.ToBase64String(prompt),
            Text: Encoding.UTF8.GetString(prompt),
            Contexts: promptContexts);
    }

    private static async Task<(byte[] TypeId, byte[] FirstObservedBy)?> ReadEntityOwnerAsync(
        NpgsqlConnection conn,
        byte[] id,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT type_id, first_observed_by FROM laplace.entities WHERE id = @id", conn);
        cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return (reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1));
    }

    private static async Task<IReadOnlyList<string>> ReadPromptContextsAsync(
        NpgsqlConnection conn,
        byte[] subjectId,
        byte[] promptSourceId,
        CancellationToken ct)
    {
        byte[] appearsIn = RelationTypeRegistry.RelationTypeId("APPEARS_IN").ToBytes();
        await using var cmd = new NpgsqlCommand("""
            SELECT DISTINCT encode(COALESCE(context_id, object_id), 'hex')
            FROM laplace.attestations
            WHERE subject_id = @subject
              AND source_id = @source
              AND type_id = @type
            ORDER BY 1
            """, conn);
        cmd.Parameters.Add("subject", NpgsqlDbType.Bytea).Value = subjectId;
        cmd.Parameters.Add("source", NpgsqlDbType.Bytea).Value = promptSourceId;
        cmd.Parameters.Add("type", NpgsqlDbType.Bytea).Value = appearsIn;
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(reader.GetString(0));
        return result;
    }

    private static (string? Name, string? Path) ParseFileMetadata(byte[] utf8)
    {
        string? name = null;
        string? path = null;
        foreach (string line in Encoding.UTF8.GetString(utf8).Split('\n'))
        {
            if (line.StartsWith("name=", StringComparison.Ordinal))
                name = line[5..];
            else if (line.StartsWith("path=", StringComparison.Ordinal))
                path = line[5..];
        }
        return (name, path);
    }

    private static byte[]? ParseEntityId(string idHex)
    {
        if (idHex.Length != 32) return null;
        try { return Convert.FromHexString(idHex); }
        catch (FormatException) { return null; }
    }

    private static Hash128 ReadHash(byte[] bytes)
    {
        if (bytes.Length != 16)
            throw new InvalidDataException($"expected 16-byte entity id, got {bytes.Length}");
        return new Hash128(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8));
    }
}
