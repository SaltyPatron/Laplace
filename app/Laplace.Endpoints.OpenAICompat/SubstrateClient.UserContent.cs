using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

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
        if (await NpgsqlSubstrateReads.HasConfirmedUserArtifactOccurrenceAsync(
                conn, requested, userArtifacts.Source.ToBytes(), ct).ConfigureAwait(false))
        {
            var vertices = await NpgsqlSubstrateReads.PackedTrajectoryVerticesAsync(
                conn, requested, ct).ConfigureAwait(false);
            var ordered = vertices.OrderBy(static v => v.Ordinal).ToArray();
            if (ordered.Length != 2) return null;

            byte[] contentIdBytes = Convert.FromHexString(ordered[0].ChildIdHex);
            byte[] metadataIdBytes = Convert.FromHexString(ordered[1].ChildIdHex);
            Hash128 contentId = ReadHash(contentIdBytes);
            Hash128 metadataId = ReadHash(metadataIdBytes);
            Hash128 documentId = contentId;

            byte[] content;
            FileMetadata fileMetadata;
            try
            {
                byte[] metadata = await NpgsqlContentReconstructor.ReconstructUtf8Async(
                    _dataSource, metadataId, ct).ConfigureAwait(false);
                fileMetadata = FileMetadata.ParseIdentityCanonicalUtf8(metadata);
                if (fileMetadata.Modality is { Length: > 0 } modality
                    && !string.Equals(
                        UserContentEndpointMappings.ResolveGrammarModality(fileMetadata.RelativePath),
                        modality,
                        StringComparison.Ordinal))
                    return null;
                content = await NpgsqlContentReconstructor.ReconstructUtf8Async(
                    _dataSource, contentId, fileMetadata.Modality, ct).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return null;
            }

            FileIdentity reconstructed;
            if (fileMetadata.Modality is { Length: > 0 } contentModality)
            {
                using var ast = GrammarDecomposer.Parse(content, contentModality);
                using var composer = new GrammarRowComposer(
                    content,
                    ast,
                    userArtifacts.Source,
                    contentModality,
                    GrammarCompositionMode.FullSource);
                OrderedCompositionComponent root = composer.RootComponent();
                if (root.Id != contentId) return null;
                reconstructed = FileEntity.Resolve(root, fileMetadata);
            }
            else
            {
                reconstructed = FileEntity.Resolve(content, fileMetadata);
            }
            if (reconstructed.FileId != ReadHash(requested)
                || reconstructed.ContentRootId != contentId
                || reconstructed.MetadataRootId != metadataId)
                return null;

            var observation = await NpgsqlSubstrateReads.UserArtifactObservationAsync(
                conn, userArtifacts.SourceName, requested, ct).ConfigureAwait(false);

            return new UserContentExportResponse(
                Kind: fileMetadata.Modality is null ? "document" : "code",
                RequestedId: idHex.ToLowerInvariant(),
                FileId: idHex.ToLowerInvariant(),
                DocumentId: Convert.ToHexStringLower(documentId.ToBytes()),
                ContentId: Convert.ToHexStringLower(contentId.ToBytes()),
                MetadataId: Convert.ToHexStringLower(metadataId.ToBytes()),
                SourceId: Convert.ToHexStringLower(userArtifacts.Source.ToBytes()),
                Source: userArtifacts.SourceName,
                Name: fileMetadata.Name,
                Path: fileMetadata.RelativePath,
                Modality: fileMetadata.Modality,
                ContentBase64: Convert.ToBase64String(content),
                Text: Encoding.UTF8.GetString(content),
                Contexts: [],
                Bytes: observation?.Bytes,
                ModifiedAt: observation?.ModifiedAt);
        }

        var promptContexts = await NpgsqlSubstrateReads.ConfirmedPromptContextsAsync(
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
            ContentId: Convert.ToHexStringLower(promptId.ToBytes()),
            MetadataId: null,
            SourceId: Convert.ToHexStringLower(conversation.PromptSource.ToBytes()),
            Source: conversation.PromptSourceName,
            Name: null,
            Path: null,
            Modality: null,
            ContentBase64: Convert.ToBase64String(prompt),
            Text: Encoding.UTF8.GetString(prompt),
            Contexts: promptContexts,
            Bytes: null,
            ModifiedAt: null);
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
