using System.Text.Json.Serialization;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// An upload: its bytes (exactly one of text or content_base64, UTF-8), its name and its relative path. A file's
/// times are no part of it, so modified_at is refused rather than ignored.
/// </summary>
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

/// <summary>
/// The upload as the substrate holds it: the file [metadata, content], the witness trunk [participant, file] that
/// holds it, the participant (source), the relative path it was named by, and its bytes.
/// </summary>
public sealed record UserContentWriteResponse(
    [property: JsonPropertyName("file_id")] string FileId,
    [property: JsonPropertyName("content_id")] string ContentId,
    [property: JsonPropertyName("metadata_id")] string MetadataId,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("bytes")] long Bytes);

/// <summary>A participant's file read back: its parts, the witness trunk that holds it, and its content.</summary>
public sealed record UserContentExportResponse(
    [property: JsonPropertyName("file_id")] string FileId,
    [property: JsonPropertyName("content_id")] string ContentId,
    [property: JsonPropertyName("metadata_id")] string MetadataId,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("content_base64")] string ContentBase64,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("bytes")] long Bytes);
