using Laplace.Engine.Core;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct UserArtifactObservation(
        long Bytes,
        DateTimeOffset? ModifiedAt);

    /// <summary>
    /// Whether one tenant source has confirmed membership of the file entity. Content
    /// identity is global; this occurrence is scoped to the source's own testimony.
    /// </summary>
    public static async Task<bool> HasConfirmedUserArtifactOccurrenceAsync(
        NpgsqlConnection connection,
        byte[] fileId,
        byte[] sourceId,
        CancellationToken ct)
    {
        bool? exists = await NpgsqlRead.ExecuteScalarAsync<bool>(connection, SqlCatalog.Get("content.confirmed_user_artifact_occurrence"),
            parameters =>
            {
                parameters.Add("source", NpgsqlDbType.Bytea).Value = sourceId;
                parameters.Add("file", NpgsqlDbType.Bytea).Value = fileId;
                parameters.Add("type", NpgsqlDbType.Bytea).Value =
                    RelationTypeRegistry.RelationTypeId(UserArtifactContent.MembershipRelation).ToBytes();
                parameters.Add("file_type", NpgsqlDbType.Bytea).Value =
                    EntityTypeRegistry.SourceFile.ToBytes();
                parameters.Add("outcome", NpgsqlDbType.Smallint).Value =
                    (short)AttestationOutcome.Confirm;
            },
            ct: ct,
            label: "confirmed_user_artifact_occurrence").ConfigureAwait(false);
        return exists is true;
    }

    private static UserArtifactObservation MapUserArtifactObservation(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));

    private static void BindUserArtifactObservation(
        NpgsqlParameterCollection parameters, string sourceName, byte[] fileId)
    {
        parameters.Add("source_name", NpgsqlDbType.Text).Value = sourceName;
        parameters.Add("file", NpgsqlDbType.Bytea).Value = fileId;
    }

    /// <summary>Size and modification time from the newest successful file-journal receipt for
    /// the file under the source.</summary>
    public static async Task<UserArtifactObservation?> UserArtifactObservationAsync(
        NpgsqlConnection connection,
        string sourceName,
        byte[] fileId,
        CancellationToken ct)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(connection, SqlCatalog.Get("content.user_artifact_observation"),
            MapUserArtifactObservation,
            parameters => BindUserArtifactObservation(parameters, sourceName, fileId),
            ct: ct,
            label: "user_artifact_observation").ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Size and modification time from the newest successful file-journal receipt for
    /// the file under the source.</summary>
    public static async Task<UserArtifactObservation?> UserArtifactObservationAsync(
        NpgsqlDataSource dataSource,
        string sourceName,
        byte[] fileId,
        CancellationToken ct)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("content.user_artifact_observation"),
            MapUserArtifactObservation,
            parameters => BindUserArtifactObservation(parameters, sourceName, fileId),
            ct: ct,
            label: "user_artifact_observation").ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Contexts (or objects, when context is null) of the prompt's confirmed
    /// membership testimony under its tenant prompt source.</summary>
    public static Task<IReadOnlyList<string>> ConfirmedPromptContextsAsync(
        NpgsqlConnection connection,
        byte[] promptId,
        byte[] promptSourceId,
        CancellationToken ct) =>
        NpgsqlRead.ReadRowsAsync(connection, SqlCatalog.Get("content.confirmed_prompt_contexts"),
            static reader => reader.GetString(0),
            parameters =>
            {
                parameters.Add("subject", NpgsqlDbType.Bytea).Value = promptId;
                parameters.Add("source", NpgsqlDbType.Bytea).Value = promptSourceId;
                parameters.Add("type", NpgsqlDbType.Bytea).Value =
                    RelationTypeRegistry.RelationTypeId(ConversationContent.MembershipRelation).ToBytes();
                parameters.Add("outcome", NpgsqlDbType.Smallint).Value =
                    (short)AttestationOutcome.Confirm;
            },
            ct: ct,
            label: "confirmed_prompt_contexts");
}
