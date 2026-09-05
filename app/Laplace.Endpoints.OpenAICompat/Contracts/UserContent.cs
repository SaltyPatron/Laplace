using System.Text.Json.Serialization;

namespace Laplace.Endpoints.OpenAICompat;

public sealed record UserTextArtifactWriteRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("content_base64")] string? ContentBase64,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("modified_at")] DateTimeOffset? ModifiedAt);

public sealed record UserCodeArtifactWriteRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("content_base64")] string? ContentBase64,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("modified_at")] DateTimeOffset? ModifiedAt);

public sealed record UserContentWriteResponse(
    [property: JsonPropertyName("file_id")] string FileId,
    [property: JsonPropertyName("document_id")] string DocumentId,
    [property: JsonPropertyName("content_id")] string ContentId,
    [property: JsonPropertyName("metadata_id")] string MetadataId,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("modality")] string? Modality);

public sealed record UserContentExportResponse(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("requested_id")] string RequestedId,
    [property: JsonPropertyName("file_id")] string? FileId,
    [property: JsonPropertyName("document_id")] string? DocumentId,
    [property: JsonPropertyName("content_id")] string ContentId,
    [property: JsonPropertyName("metadata_id")] string? MetadataId,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("modality")] string? Modality,
    [property: JsonPropertyName("content_base64")] string ContentBase64,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("contexts")] IReadOnlyList<string> Contexts,
    [property: JsonPropertyName("bytes")] long? Bytes,
    [property: JsonPropertyName("modified_at")] DateTimeOffset? ModifiedAt);
